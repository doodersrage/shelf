using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record KoreaderImport(int Added, int AlreadyHere, int Books, IReadOnlyList<string> NotFound);

// Highlights made in KOReader, from its Export highlights as JSON (exporter.koplugin): one book, or "documents" for
// several. A book is matched by its title, then by the name of its file; each highlight goes to the chapter its
// position names (DocFragment[n], the nth file of the spine) or, for a PDF, its page, and is checked against the
// chapter's own words so it lands where the reader will find it. A highlight already here is not added twice.
public static partial class KoreaderHighlights
{
    public const long MaxBytes = 10 * 1024 * 1024;
    private const int Around = 32;

    public static async Task<KoreaderImport> ImportAsync(ShelfDb db, EbookStore store, Stream json, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(json, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var exported = root.TryGetProperty("documents", out var many) && many.ValueKind == JsonValueKind.Array ? many.EnumerateArray().ToList() : [root];

        var me = db.ReaderId;
        var books = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(book => book.EbookStoredName != null && (book.OwnerId == me || book.BorrowerId == me))
            .Select(book => new { book.Id, book.Title, book.Author, book.EbookFileName, book.EbookStoredName, Borrowed = book.BorrowerId == me && book.OwnerId != me })
            .ToListAsync(cancellationToken);

        int added = 0, here = 0, matched = 0;
        var notFound = new List<string>();
        foreach (var exportedBook in exported)
        {
            var title = Text(exportedBook, "title") ?? "";
            var author = Text(exportedBook, "author") ?? "";
            var file = Path.GetFileName(Text(exportedBook, "file") ?? "");
            var byTitle = books.Where(book => Same(book.Title, title)).ToList();
            var book = byTitle.FirstOrDefault(candidate => Same(candidate.Author, author)) ?? byTitle.FirstOrDefault()
                ?? books.FirstOrDefault(candidate => file.Length > 0 && string.Equals(candidate.EbookFileName, file, StringComparison.OrdinalIgnoreCase));
            var entries = exportedBook.TryGetProperty("entries", out var listed) && listed.ValueKind == JsonValueKind.Array ? listed.EnumerateArray().ToList() : [];
            if (book is null)
            {
                if (entries.Count > 0)
                {
                    notFound.Add(title.Length > 0 ? title : file);
                }

                continue;
            }

            matched++;
            var path = store.OpenPath(book.EbookStoredName);
            var pdf = EbookStore.IsPdf(book.EbookStoredName);
            var chapters = !pdf && path is not null ? Chapters(path) : [];
            var ownerMark = book.Borrowed ? (int?)me : null;
            var existing = await db.Highlights.Where(mark => mark.BookId == book.Id && mark.ReaderId == ownerMark).Select(mark => new { mark.ChapterIndex, mark.Text }).ToListAsync(cancellationToken);
            var known = existing.Select(mark => (mark.ChapterIndex, mark.Text)).ToHashSet();

            foreach (var entry in entries)
            {
                var text = Text(entry, "text");
                if (string.IsNullOrWhiteSpace(text) || (Text(entry, "sort") is { } sort && sort != "highlight"))
                {
                    continue;
                }

                text = text.Trim();
                if (text.Length > BookRules.MaxHighlightLength)
                {
                    text = text[..BookRules.MaxHighlightLength];
                }

                var (chapter, prefix, suffix) = pdf ? (Page(entry), null, null) : Locate(chapters, Position(entry), text);
                if (chapter < 0 || !known.Add((chapter, text)))
                {
                    here += chapter < 0 ? 0 : 1;
                    continue;
                }

                var note = Text(entry, "note")?.Trim();
                db.Highlights.Add(new Highlight
                {
                    BookId = book.Id,
                    ReaderId = ownerMark,
                    ChapterIndex = chapter,
                    Text = text,
                    Note = string.IsNullOrEmpty(note) ? null : note[..Math.Min(note.Length, BookRules.MaxHighlightNoteLength)],
                    Prefix = prefix,
                    Suffix = suffix,
                    NotedAt = entry.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number
                        ? DateTimeOffset.FromUnixTimeSeconds(time.GetInt64())
                        : DateTimeOffset.UtcNow,
                });
                added++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (added > 0)
        {
            await Audit.NoteAsync(db, Say("Brought highlights from KOReader"), detail: added.ToString(CultureInfo.InvariantCulture), cancellationToken: cancellationToken);
        }

        return new KoreaderImport(added, here, matched, notFound);
    }

    public static async Task<IResult> Upload(IFormFile? file, ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        if (file is null || file.Length is 0 or > MaxBytes)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [T("Choose the JSON file KOReader exported, of up to 10 MB.")] });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return TypedResults.Ok(await ImportAsync(db, store, stream, cancellationToken));
        }
        catch (JsonException)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [T("That is not a highlights file from KOReader. In KOReader, export highlights as JSON.")] });
        }
    }

    // The words of each chapter, as one line of text, to find a highlight in.
    private static List<string> Chapters(string path)
    {
        var count = EpubFile.Chapters(path)?.Count ?? 0;
        return Enumerable.Range(0, count).Select(index => EpubFile.ChapterHtml(path, index, "") is { } html ? Search.PlainText(html) : "").ToList();
    }

    // The chapter a highlight is in, and the words around it. The position KOReader gives comes first; when the words
    // are not there (a different edition, say), every chapter is searched.
    private static (int Chapter, string? Prefix, string? Suffix) Locate(List<string> chapters, int? hinted, string text)
    {
        var wanted = Spaces().Replace(text, " ");
        var order = hinted is int hint && hint >= 0 && hint < chapters.Count
            ? new[] { hint }.Concat(Enumerable.Range(0, chapters.Count).Where(index => index != hint))
            : Enumerable.Range(0, chapters.Count);
        foreach (var index in order)
        {
            var at = chapters[index].IndexOf(wanted, StringComparison.Ordinal);
            if (at >= 0)
            {
                var words = chapters[index];
                return (index, words[Math.Max(0, at - Around)..at], words[(at + wanted.Length)..Math.Min(words.Length, at + wanted.Length + Around)]);
            }
        }

        return (hinted ?? (chapters.Count > 0 ? -1 : 0), null, null);
    }

    // DocFragment[n] in the XPointer KOReader keeps (pn_xp, or page in older exports).
    private static int? Position(JsonElement entry)
    {
        foreach (var name in new[] { "pn_xp", "page" })
        {
            if (Text(entry, name) is { } value && DocFragment().Match(value) is { Success: true } fragment
                && int.TryParse(fragment.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var spine) && spine >= 1)
            {
                return spine - 1;
            }
        }

        return null;
    }

    private static int Page(JsonElement entry)
    {
        foreach (var name in new[] { "pn_xp", "page" })
        {
            if (entry.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 1)
                {
                    return number - 1;
                }

                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1)
                {
                    return parsed - 1;
                }
            }
        }

        return -1;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static bool Same(string? a, string? b) => Simple(a) is { Length: > 0 } left && left == Simple(b);

    private static string Simple(string? text) => Letters().Replace((text ?? "").ToLowerInvariant(), "");

    [GeneratedRegex(@"DocFragment\[(\d+)\]")]
    private static partial Regex DocFragment();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Letters();
}
