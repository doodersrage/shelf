using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public sealed record CollectionExport(string Name, string? Description, DateOnly? ReadBy, CollectionEntryExport[] Books);

public sealed record CollectionEntryExport(int Book, string Title, string Author, string? Note);

public sealed record BorrowedHighlightExport(string Title, string Author, string Owner, int ChapterIndex, string Text, string? Note, DateTimeOffset NotedAt);

// The rest of a reader's own records, beside shelf.json in their download, in plain formats described by the README
// that comes with them: a spreadsheet of the books, their collections, the loans of their books, and what they
// marked in books they borrowed. Only ever this reader's: another member of the household is never in it.
public static class PortableExport
{
    public static async Task WriteAsync(ShelfDb db, ZipArchive zip, IReadOnlyList<Book> books, JsonSerializerOptions json, ArchiveManifest manifest, CancellationToken cancellationToken)
    {
        var place = books.Select((book, index) => (book.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);

        await WriteTextAsync(manifest, zip, "README.md", Readme);
        var fields = (await db.CustomFields.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(field => field.Position).ThenBy(field => field.Id).ToList();
        var values = (await db.CustomValues.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(value => (value.BookId, value.FieldId), value => value.Value);
        await WriteTextAsync(manifest, zip, "books.csv", Csv(books, fields, values));

        var collections = await db.Collections.AsNoTracking().Where(collection => collection.OwnerId == db.ReaderId)
            .Include(collection => collection.Books).ThenInclude(entry => entry.Book)
            .OrderBy(collection => collection.Name).ToListAsync(cancellationToken);
        await WriteJsonAsync(manifest, zip, "collections.json", collections.Select(collection => new CollectionExport(collection.Name, collection.Description, collection.ReadBy,
            [.. collection.Books.Where(entry => entry.Book is not null && place.ContainsKey(entry.BookId)).OrderBy(entry => entry.Position)
                .Select(entry => new CollectionEntryExport(place[entry.BookId], entry.Book!.Title, entry.Book.Author, entry.Note))])).ToArray(), json, cancellationToken);

        var loans = await db.LoanRecords.AsNoTracking().OrderBy(loan => loan.LentOn).ThenBy(loan => loan.Id)
            .Select(loan => new { Loan = loan, loan.Book!.Title }).ToListAsync(cancellationToken);
        await WriteJsonAsync(manifest, zip, "loans.json", loans.Select(item => new
        {
            Book = place.TryGetValue(item.Loan.BookId, out var at) ? at : (int?)null,
            item.Title, item.Loan.Borrower, item.Loan.LentOn, item.Loan.DueOn, item.Loan.ReturnedOn, item.Loan.Renewals,
            item.Loan.ConditionOut, item.Loan.ConditionBack, item.Loan.Note,
        }).ToArray(), json, cancellationToken);

        // Kept on the owner's book while it was lent, but written by this reader, and theirs to take.
        var me = db.ReaderId;
        var marks = await db.Highlights.IgnoreQueryFilters().AsNoTracking()
            .Where(mark => mark.ReaderId == me && mark.Book!.OwnerId != me)
            .OrderBy(mark => mark.Id)
            .Select(mark => new { mark.Book!.Title, mark.Book.Author, mark.Book.OwnerId, mark.ChapterIndex, mark.Text, mark.Note, mark.NotedAt })
            .ToListAsync(cancellationToken);
        var ownerIds = marks.Select(mark => mark.OwnerId).OfType<int>().Distinct().ToList();
        var owners = await db.Readers.AsNoTracking().Where(reader => ownerIds.Contains(reader.Id)).ToDictionaryAsync(reader => reader.Id, reader => reader.Name, cancellationToken);
        await WriteJsonAsync(manifest, zip, "borrowed-highlights.json", marks.Select(mark => new BorrowedHighlightExport(mark.Title, mark.Author,
            mark.OwnerId is int owner ? owners.GetValueOrDefault(owner, "") : "", mark.ChapterIndex, mark.Text, mark.Note, mark.NotedAt)).ToArray(), json, cancellationToken);
    }

    private static readonly string[] Columns =
        ["Number", "Title", "Subtitle", "Author", "ISBN", "Year", "Pages", "Publisher", "Language", "Format", "Series", "Series number", "Status", "Rating", "Loved",
         "Started", "Finished", "Added", "Place", "Tags", "Notes"];

    // One row a book, numbered as in shelf.json, with dates as yyyy-MM-dd whatever the reader's language.
    // The reader's own fields come last, a column each, as kept: numbers as 1234.5, dates as yyyy-MM-dd, yes or no.
    public static string Csv(IReadOnlyList<Book> books, IReadOnlyList<CustomField>? fields = null, IReadOnlyDictionary<(int, int), string>? values = null)
    {
        fields ??= [];
        var text = new StringBuilder();
        text.AppendLine(string.Join(',', Columns.Concat(fields.Select(field => field.Name)).Select(Field)));
        for (var index = 0; index < books.Count; index++)
        {
            var book = books[index];
            string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string? Number(int? number) => number?.ToString(CultureInfo.InvariantCulture);
            text.AppendLine(string.Join(',', new[]
            {
                Number(index + 1), book.Title, book.Subtitle, book.Author, book.Isbn, Number(book.Year), Number(book.Pages), book.Publisher, book.Language,
                book.Format?.ToString(), book.Series, Number(book.SeriesNumber), book.Status.ToString(), Number(book.Rating), book.Loved ? "yes" : "",
                Date(book.StartedOn), Date(book.FinishedOn), book.AddedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), book.Location,
                string.Join("; ", book.Tags.Select(tag => tag.Name).Order(StringComparer.Ordinal)), book.Notes,
            }.Concat(fields.Select(field => values?.GetValueOrDefault((book.Id, field.Id)))).Select(Field)));
        }

        return text.ToString();
    }

    // Quoted when it must be, as RFC 4180 has it; a leading = + - @ is set off so a spreadsheet never runs it.
    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value[0] is '=' or '+' or '-' or '@' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? '"' + value.Replace("\"", "\"\"") + '"' : value;
    }

    private static async Task WriteTextAsync(ArchiveManifest manifest, ZipArchive zip, string name, string text)
    {
        await using var writer = new StreamWriter(manifest.Create(zip, name), new UTF8Encoding(false));
        await writer.WriteAsync(text);
    }

    private static async Task WriteJsonAsync<T>(ArchiveManifest manifest, ZipArchive zip, string name, T value, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        await using var stream = manifest.Create(zip, name);
        await JsonSerializer.SerializeAsync(stream, value, json, cancellationToken);
    }

    private const string Readme = """
        # Your shelf

        Everything of yours on Shelf, in formats any program can read. Nothing in it belongs to anyone else on the shelf.

        - `shelf.json` — every book, with its reads, sessions, quotes, highlights, notes, and tags; your journal; the links
          between books; and your goals. Restoring the zip, under Backup, on this shelf or another, brings this file back
          in, with `collections.json` and the returned loans in `loans.json`.
        - `books.csv` — the books again as a spreadsheet, a row each. Dates are written year-month-day. "Number" is the
          book's place in `shelf.json`, counting from 1. Your own fields come last, a column each.
        - `collections.json` — your collections, with the note on each book. `book` counts from 0 in `shelf.json`.
        - `loans.json` — each time one of your books was lent: to whom, when, how often renewed, and the state it went out
          and came back in.
        - `borrowed-highlights.json` — what you marked in books you borrowed from others. It is yours to keep, but is not
          brought back in: those books are other readers'.
        - `books/0001/` and on — the e-book, audiobook tracks, and cover picture of each book, numbered as in `books.csv`,
          with the book's other e-book formats under `formats/`.
        - `manifest.json` — every other file with its size and SHA-256, to show the download is whole; `SCHEMA.md` says
          what each file holds, field by field.

        Text is UTF-8. JSON keys are in camelCase; dates are ISO 8601.
        """;
}
