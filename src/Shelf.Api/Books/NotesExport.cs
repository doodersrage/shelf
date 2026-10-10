using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A book's quotes, highlights, notes, and review as Markdown, for Obsidian, Notion, or a plain folder of notes.
// A borrower gets only what they wrote themselves.
public static class NotesExport
{
    public static async Task<IResult> Book(int id, ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open)
        {
            return TypedResults.NotFound();
        }

        var markdown = await MarkdownAsync(db, store, open, cancellationToken);
        return Results.File(Encoding.UTF8.GetBytes(markdown), "text/markdown; charset=utf-8", FileName(open.Book));
    }

    // Every book with something written about it, one Markdown file each, in a zip.
    public static async Task<IResult> All(ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        var me = db.ReaderId;
        var ids = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(book => (book.OwnerId == me && (book.Quotes.Any() || db.Highlights.Any(mark => mark.BookId == book.Id && mark.ReaderId == null)
                    || book.Review != null || book.Notes != null))
                || (book.BorrowerId == me && db.Highlights.Any(mark => mark.BookId == book.Id && mark.ReaderId == me)))
            .OrderBy(book => book.Author).ThenBy(book => book.Title)
            .Select(book => book.Id)
            .ToListAsync(cancellationToken);

        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open)
                {
                    continue;
                }

                var name = FileName(open.Book);
                for (var n = 2; !used.Add(name); n++)
                {
                    name = $"{Path.GetFileNameWithoutExtension(FileName(open.Book))} ({n}).md";
                }

                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                await writer.WriteAsync(await MarkdownAsync(db, store, open, cancellationToken));
            }
        }

        memory.Position = 0;
        return Results.File(memory, "application/zip", $"shelf-notes-{DateTime.UtcNow:yyyy-MM-dd}.zip");
    }

    public static async Task<string> MarkdownAsync(ShelfDb db, EbookStore store, OpenBook open, CancellationToken cancellationToken)
    {
        var book = open.Book;
        var marks = await Lending.Marks(db, open).AsNoTracking()
            .OrderBy(mark => mark.ChapterIndex).ThenBy(mark => mark.Id)
            .ToListAsync(cancellationToken);
        var quotes = open.Borrowed
            ? []
            : await db.Quotes.AsNoTracking().Where(quote => quote.BookId == book.Id).OrderBy(quote => quote.Page).ThenBy(quote => quote.Id).ToListAsync(cancellationToken);
        var pdf = EbookStore.IsPdf(book.EbookStoredName);
        var chapters = EbookStore.IsEpub(book.EbookStoredName) ? EpubFile.Chapters(store.OpenPath(book.EbookStoredName) ?? "") : null;

        var text = new StringBuilder();
        text.AppendLine("---");
        text.AppendLine($"title: {Yaml(book.Title)}");
        text.AppendLine($"author: {Yaml(book.Author)}");
        if (book.Isbn is { } isbn) text.AppendLine($"isbn: {Yaml(isbn)}");
        if (book.Year is { } year) text.AppendLine($"year: {year}");
        if (!open.Borrowed && book.Rating is { } rating) text.AppendLine($"rating: {rating}");
        text.AppendLine($"status: {book.Status}");
        if (!open.Borrowed && book.FinishedOn is { } finished) text.AppendLine($"finished: {finished:yyyy-MM-dd}");
        text.AppendLine("source: Shelf");
        text.AppendLine("---");
        text.AppendLine();
        text.AppendLine($"# {Inline(book.Title)}");
        text.AppendLine();
        text.AppendLine(T("by {0}", Inline(book.Author)));

        if (!open.Borrowed && !string.IsNullOrWhiteSpace(book.Review))
        {
            text.AppendLine().AppendLine("## " + T("Review")).AppendLine().AppendLine(book.Review.Trim());
        }

        if (!open.Borrowed && !string.IsNullOrWhiteSpace(book.Notes))
        {
            text.AppendLine().AppendLine("## " + T("Notes")).AppendLine().AppendLine(book.Notes.Trim());
        }

        if (quotes.Count > 0)
        {
            text.AppendLine().AppendLine("## " + T("Quotes"));
            foreach (var quote in quotes)
            {
                text.AppendLine().Append(Blockquote(quote.Text));
                if (quote.Page is { } page)
                {
                    text.AppendLine(">").AppendLine("> — " + T("page {0}", page));
                }
            }
        }

        if (marks.Count > 0)
        {
            text.AppendLine().AppendLine("## " + T("Highlights"));
            int? section = null;
            foreach (var mark in marks)
            {
                if (mark.ChapterIndex != section)
                {
                    section = mark.ChapterIndex;
                    var heading = pdf
                        ? T("Page {0}", mark.ChapterIndex + 1)
                        : chapters is not null && mark.ChapterIndex < chapters.Count ? chapters[mark.ChapterIndex].Title : T("Chapter {0}", mark.ChapterIndex + 1);
                    text.AppendLine().AppendLine($"### {Inline(heading)}");
                }

                text.AppendLine().Append(Blockquote(mark.Text));
                if (!string.IsNullOrWhiteSpace(mark.Note))
                {
                    text.AppendLine().AppendLine(mark.Note.Trim());
                }
            }
        }

        return text.ToString();
    }

    private static string Blockquote(string passage) =>
        string.Concat(passage.Trim().Replace("\r\n", "\n").Split('\n').Select(line => line.Length == 0 ? ">\n" : $"> {line}\n"));

    private static string Inline(string text) => text.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Yaml(string text) => "\"" + Inline(text).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string FileName(Book book)
    {
        var name = $"{book.Author} - {book.Title}";
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var replaced = new string(name.Select(character => invalid.Contains(character) || char.IsControl(character) ? ' ' : character).ToArray());
        var clean = System.Text.RegularExpressions.Regex.Replace(replaced, " {2,}", " ").Trim().TrimEnd('.');
        return (clean.Length > 150 ? clean[..150].Trim() : clean) + ".md";
    }
}
