using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record PublicBook(int Id, string Title, string Author, string? Cover, DateOnly? FinishedOn, int? Rating);

public sealed record PublicShelf(string Reader, PublicBook[] Reading, PublicBook[] Finished, int FinishedThisYear, int Goal);

// What a reader lets out of the shelf by address: a calendar of their loans' due dates, for a calendar app to
// subscribe to, and a public page of what they are reading, with a widget for a website and a feed of books finished.
public static class Feeds
{
    public const int Shown = 12;

    public static void Map(WebApplication app)
    {
        app.MapGet("/calendar/{token}.ics", Calendar).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/public/{token}/widget", Widget).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/public/{token}/feed.xml", Atom).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/public/{token}/shelf.json", async (string token, ShelfDb db, CancellationToken cancellationToken) =>
            await PublicAsync(db, token, cancellationToken) is { } shelf ? Results.Ok(shelf) : Results.NotFound()).AllowAnonymous().WithTags("Sharing");
        app.MapGet("/public/{token}/covers/{id:int}", Cover).AllowAnonymous().ExcludeFromDescription();
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // A new calendar address, shown once; the one before stops working.
    public static async Task<string> MakeCalendarAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        reader.CalendarTokenHash = Hash(token);
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

    public static async Task StopCalendarAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        await db.Readers.Where(item => item.Id == db.ReaderId).ExecuteUpdateAsync(set => set.SetProperty(item => item.CalendarTokenHash, (string?)null), cancellationToken);

    // The public page on or off; its address stays the same until it is turned off.
    public static async Task<string?> SetPublicAsync(ShelfDb db, bool on, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.FirstAsync(item => item.Id == db.ReaderId, cancellationToken);
        reader.PublicToken = on && !reader.Restricted ? reader.PublicToken ?? WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(12)) : null;
        await db.SaveChangesAsync(cancellationToken);
        return reader.PublicToken;
    }

    // The loans' due dates, each a day in the calendar: books lent out, books borrowed, and copies from linked shelves.
    private static async Task<IResult> Calendar(string token, ShelfDb db, CancellationToken cancellationToken)
    {
        var hash = Hash(token);
        var reader = token.Length is > 10 and < 60 ? await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.CalendarTokenHash == hash, cancellationToken) : null;
        if (reader is null)
        {
            return Results.NotFound();
        }

        var lent = await db.Books.IgnoreQueryFilters().AsNoTracking().Where(book => book.OwnerId == reader.Id && book.LoanedTo != null && book.DueOn != null)
            .Select(book => new { book.Id, book.Title, book.LoanedTo, Due = book.DueOn!.Value }).ToListAsync(cancellationToken);
        var borrowed = await db.Books.IgnoreQueryFilters().AsNoTracking().Where(book => book.BorrowerId == reader.Id && book.DueOn != null)
            .Select(book => new { book.Id, book.Title, Owner = book.Owner!.Name, Due = book.DueOn!.Value }).ToListAsync(cancellationToken);
        var visiting = await db.Books.IgnoreQueryFilters().AsNoTracking().Where(book => book.OwnerId == reader.Id && book.VisitingDueOn != null)
            .Select(book => new { book.Id, book.Title, Owner = book.VisitingOwner ?? "", Due = book.VisitingDueOn!.Value }).ToListAsync(cancellationToken);

        var events = new List<(string Uid, DateOnly Day, string Summary)>();
        using (Speaking(reader.Language))
        {
            events.AddRange(lent.Select(book => ($"lent-{book.Id}", book.Due, T("{0} is due back from {1}", book.Title, book.LoanedTo!))));
            events.AddRange(borrowed.Concat(visiting).Select(book => ($"borrowed-{book.Id}", book.Due, T("Give {0} back to {1}", book.Title, book.Owner))));
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var ics = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Shelf//Loans//EN\r\nCALSCALE:GREGORIAN\r\nX-WR-CALNAME:")
            .Append(Escape(T("Loans on Shelf"))).Append("\r\n");
        foreach (var (uid, day, summary) in events)
        {
            ics.Append("BEGIN:VEVENT\r\n")
                .Append("UID:").Append(uid).Append("-").Append(reader.Id).Append("@shelf\r\n")
                .Append("DTSTAMP:").Append(stamp).Append("\r\n")
                .Append("DTSTART;VALUE=DATE:").Append(day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("DTEND;VALUE=DATE:").Append(day.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("SUMMARY:").Append(Escape(summary)).Append("\r\n")
                .Append("END:VEVENT\r\n");
        }

        ics.Append("END:VCALENDAR\r\n");
        return Results.Text(ics.ToString(), "text/calendar; charset=utf-8");
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");

    // The page's books: those being read, and those finished most lately. Never notes, reviews, or loans.
    public static async Task<PublicShelf?> PublicAsync(ShelfDb db, string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 40)
        {
            return null;
        }

        var reader = await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.PublicToken == token && !item.Restricted, cancellationToken);
        if (reader is null)
        {
            return null;
        }

        var books = await db.Books.IgnoreQueryFilters().AsNoTracking().Where(book => book.OwnerId == reader.Id && (book.Status == BookStatus.Reading || book.Status == BookStatus.Finished)).ToListAsync(cancellationToken);
        PublicBook Show(Book book) => new(book.Id, book.Title, book.Author,
            book.CoverImage is not null || (string.IsNullOrWhiteSpace(book.CoverUrl) && book.FileCover) ? $"/public/{token}/covers/{book.Id}" : book.CoverUrl, book.FinishedOn, book.Rating);
        var year = DateTime.UtcNow.Year;
        var goal = await db.Settings.AsNoTracking().Where(setting => setting.Id == reader.Id).Select(setting => setting.YearlyGoal).FirstOrDefaultAsync(cancellationToken);
        return new PublicShelf(
            reader.Name,
            books.Where(book => book.Status == BookStatus.Reading).OrderByDescending(book => book.StartedOn ?? DateOnly.MinValue).Take(Shown).Select(Show).ToArray(),
            books.Where(book => book.Status == BookStatus.Finished && book.FinishedOn is not null).OrderByDescending(book => book.FinishedOn).Take(Shown).Select(Show).ToArray(),
            books.Count(book => book.Status == BookStatus.Finished && book.FinishedOn?.Year == year),
            goal);
    }

    // A small page to put in a frame on a website: what the reader is reading now.
    private static async Task<IResult> Widget(string token, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await PublicAsync(db, token, cancellationToken) is not { } shelf)
        {
            return Results.NotFound();
        }

        string E(string text) => WebUtility.HtmlEncode(text);
        var items = string.Concat(shelf.Reading.Take(4).Select(book =>
            $"<li>{(book.Cover is { } cover ? $"<img src=\"{E(cover)}\" alt=\"\">" : "")}<span><strong>{E(book.Title)}</strong><br>{E(book.Author)}</span></li>"));
        var html = $$"""
            <!doctype html><html lang="{{Current}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="robots" content="noindex"><title>{{E(T("{0} is reading", shelf.Reader))}}</title>
            <style>
            :root { color-scheme: light dark; }
            body { margin: 0; padding: 12px; font: 14px/1.4 system-ui, sans-serif; background: transparent; }
            h1 { font-size: 14px; margin: 0 0 8px; }
            ul { list-style: none; margin: 0; padding: 0; display: grid; gap: 8px; }
            li { display: flex; gap: 8px; align-items: center; }
            img { width: 40px; height: 60px; object-fit: cover; border-radius: 3px; }
            a { color: inherit; }
            </style></head><body>
            <h1><a href="/public/{{E(token)}}" target="_blank" rel="noopener">{{E(T("{0} is reading", shelf.Reader))}}</a></h1>
            {{(shelf.Reading.Length == 0 ? $"<p>{E(T("Nothing just now."))}</p>" : $"<ul>{items}</ul>")}}
            </body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    // Books finished, newest first, for a feed reader.
    private static async Task<IResult> Atom(string token, ShelfDb db, HttpContext http, CancellationToken cancellationToken)
    {
        if (await PublicAsync(db, token, cancellationToken) is not { } shelf)
        {
            return Results.NotFound();
        }

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var site = $"{http.Request.Scheme}://{http.Request.Host}";
        var feed = new XElement(atom + "feed",
            new XElement(atom + "title", T("Books {0} has finished", shelf.Reader)),
            new XElement(atom + "id", $"{site}/public/{token}"),
            new XElement(atom + "link", new XAttribute("href", $"{site}/public/{token}")),
            new XElement(atom + "updated", (shelf.Finished.FirstOrDefault()?.FinishedOn?.ToDateTime(TimeOnly.MinValue) ?? DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            new XElement(atom + "author", new XElement(atom + "name", shelf.Reader)),
            shelf.Finished.Select(book => new XElement(atom + "entry",
                new XElement(atom + "title", T("{0}, by {1}", book.Title, book.Author)),
                new XElement(atom + "id", $"{site}/public/{token}#book-{book.Id}"),
                new XElement(atom + "updated", book.FinishedOn!.Value.ToDateTime(TimeOnly.MinValue).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                new XElement(atom + "summary", book.Rating is int stars ? T("Finished, with {0} of 5 stars.", stars) : T("Finished.")))));
        return Results.Content(new XDocument(feed).ToString(), "application/atom+xml; charset=utf-8");
    }

    private static async Task<IResult> Cover(string token, int id, ShelfDb db, EbookStore store, CoverStore covers, HttpContext http, CancellationToken cancellationToken)
    {
        var shelf = await PublicAsync(db, token, cancellationToken);
        if (shelf is null || !shelf.Reading.Concat(shelf.Finished).Any(book => book.Id == id))
        {
            return Results.NotFound();
        }

        var book = await db.Books.IgnoreQueryFilters().AsNoTracking().FirstAsync(item => item.Id == id, cancellationToken);
        return Covers.Serve(book.EbookStoredName, book.CoverImage, store, covers, http);
    }
}
