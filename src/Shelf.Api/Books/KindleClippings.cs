using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Readers;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record KindleImport(int Highlights, int Quotes, int AlreadyHere, int Books, IReadOnlyList<string> NotFound);

// A Kindle's "My Clippings.txt": every highlight, note, and bookmark made on it, one after another, each closed by a
// line of equals signs. A clipping's book is matched by title and author. On a book with an EPUB the highlight goes
// to the chapter that has its words, with any note made at the same place; on any other book it becomes a quote,
// with its page when the Kindle gave one. Clippings already on the shelf are not added twice.
public static partial class KindleClippings
{
    public const long MaxBytes = 20 * 1024 * 1024;

    public sealed record Clipping(string Title, string Author, string Kind, string Text, int? Page, int? LocationStart, int? LocationEnd, DateTimeOffset? AddedAt);

    public static IReadOnlyList<Clipping> Parse(string text)
    {
        var clippings = new List<Clipping>();
        foreach (var block in text.TrimStart('﻿').Split("==========", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Replace("\r", "", StringComparison.Ordinal).Split('\n').Select(line => line.Trim('﻿', ' ', '\t')).ToList();
            while (lines.Count > 0 && lines[0].Length == 0)
            {
                lines.RemoveAt(0);
            }

            if (lines.Count < 2)
            {
                continue;
            }

            var (title, author) = Heading(lines[0]);
            var meta = lines[1];
            var body = string.Join('\n', lines.Skip(2)).Trim();
            var kind = HighlightWord().IsMatch(meta) ? "highlight" : NoteWord().IsMatch(meta) ? "note" : BookmarkWord().IsMatch(meta) ? "bookmark" : "highlight";
            var page = PageNumber().Match(meta) is { Success: true } pageMatch && int.TryParse(pageMatch.Groups[1].Value, out var number) ? number : (int?)null;
            int? start = null, end = null;
            if (LocationNumbers().Match(meta) is { Success: true } location && int.TryParse(location.Groups[1].Value, out var from))
            {
                start = from;
                end = location.Groups[2].Success && int.TryParse(location.Groups[2].Value, out var to)
                    ? to < from ? int.Parse(location.Groups[1].Value[..^location.Groups[2].Value.Length] + location.Groups[2].Value, CultureInfo.InvariantCulture) : to
                    : from;
            }

            clippings.Add(new Clipping(title, author, kind, body, page, start, end, Added(meta)));
        }

        return clippings;
    }

    public static async Task<KindleImport> ImportAsync(ShelfDb db, EbookStore store, string text, CancellationToken cancellationToken)
    {
        var clippings = Parse(text).Where(clipping => clipping.Kind != "bookmark" && clipping.Text.Length > 0).ToList();
        var books = await db.Books.AsNoTracking().Select(book => new { book.Id, book.Title, book.Subtitle, book.Author, book.EbookStoredName }).ToListAsync(cancellationToken);
        int highlights = 0, quotes = 0, here = 0;
        var matched = new HashSet<int>();
        var notFound = new List<string>();

        foreach (var group in clippings.GroupBy(clipping => (clipping.Title, clipping.Author)))
        {
            var byTitle = books.Where(book => SameTitle(book.Title, book.Subtitle, group.Key.Title)).ToList();
            var book = byTitle.FirstOrDefault(candidate => SameAuthor(candidate.Author, group.Key.Author)) ?? (byTitle.Count == 1 ? byTitle[0] : null);
            if (book is null)
            {
                notFound.Add(group.Key.Title);
                continue;
            }

            matched.Add(book.Id);
            var path = EbookStore.IsEpub(book.EbookStoredName) ? store.OpenPath(book.EbookStoredName) : null;
            var chapters = path is null ? [] : KoreaderHighlights.Chapters(path);
            var knownMarks = (await db.Highlights.Where(mark => mark.BookId == book.Id && mark.ReaderId == null).Select(mark => new { mark.ChapterIndex, mark.Text }).ToListAsync(cancellationToken))
                .Select(mark => (mark.ChapterIndex, mark.Text)).ToHashSet();
            var knownQuotes = (await db.Quotes.Where(quote => quote.BookId == book.Id).Select(quote => quote.Text).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            var notes = group.Where(clipping => clipping.Kind == "note").ToList();

            foreach (var clipping in group.Where(clipping => clipping.Kind == "highlight"))
            {
                var words = clipping.Text.Length > BookRules.MaxHighlightLength ? clipping.Text[..BookRules.MaxHighlightLength] : clipping.Text;
                // A note made at the end of a highlight belongs to it.
                var note = notes.FirstOrDefault(item => item.LocationStart is int at && clipping.LocationStart is int from && clipping.LocationEnd is int to && at >= from && at <= to)?.Text;
                var located = chapters.Count > 0 ? KoreaderHighlights.Locate(chapters, null, words) : (Chapter: -1, Prefix: null, Suffix: null);
                if (located.Chapter >= 0 && located.Prefix is not null)
                {
                    if (!knownMarks.Add((located.Chapter, words)))
                    {
                        here++;
                        continue;
                    }

                    db.Highlights.Add(new Highlight
                    {
                        BookId = book.Id,
                        ChapterIndex = located.Chapter,
                        Text = words,
                        Note = note is null ? null : note[..Math.Min(note.Length, BookRules.MaxHighlightNoteLength)],
                        Prefix = located.Prefix,
                        Suffix = located.Suffix,
                        NotedAt = clipping.AddedAt ?? DateTimeOffset.UtcNow,
                    });
                    highlights++;
                }
                else
                {
                    var quote = words.Length > 2000 ? words[..2000] : words;
                    if (!knownQuotes.Add(quote))
                    {
                        here++;
                        continue;
                    }

                    db.Quotes.Add(new Quote { BookId = book.Id, Text = quote, Page = clipping.Page is >= 1 and <= 20000 ? clipping.Page : null, NotedAt = clipping.AddedAt ?? DateTimeOffset.UtcNow });
                    quotes++;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (highlights + quotes > 0)
        {
            await Audit.NoteAsync(db, Say("Brought highlights from a Kindle"), detail: (highlights + quotes).ToString(CultureInfo.InvariantCulture), cancellationToken: cancellationToken);
        }

        return new KindleImport(highlights, quotes, here, matched.Count, notFound.Distinct().ToList());
    }

    public static async Task<IResult> Upload(IFormFile? file, ShelfDb db, EbookStore store, CancellationToken cancellationToken)
    {
        if (file is null || file.Length is 0 or > MaxBytes)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [T("Choose the My Clippings.txt file from the Kindle's documents folder, of up to 20 MB.")] });
        }

        using var reader = new StreamReader(file.OpenReadStream());
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (!text.Contains("==========", StringComparison.Ordinal))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [T("That is not a Kindle's My Clippings.txt.")] });
        }

        return TypedResults.Ok(await ImportAsync(db, store, text, cancellationToken));
    }

    // "Title (Author)", where the title may itself end in brackets; the last bracketed part is the author.
    private static (string Title, string Author) Heading(string line)
    {
        var match = TitleAuthor().Match(line);
        return match.Success ? (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim()) : (line.Trim(), "");
    }

    private static DateTimeOffset? Added(string meta)
    {
        var last = meta.Split('|').Last();
        var text = AddedWords().Replace(last, "").Trim().TrimStart(',').Trim();
        foreach (var culture in new[] { "en-US", "en-GB", "de-DE", "fr-FR", "es-ES", "it-IT" })
        {
            if (DateTime.TryParse(text, CultureInfo.GetCultureInfo(culture), DateTimeStyles.AssumeLocal, out var when))
            {
                return new DateTimeOffset(when);
            }
        }

        return null;
    }

    private static bool SameTitle(string title, string? subtitle, string clipped)
    {
        var mine = Simple(title);
        var theirs = Simple(clipped);
        var theirsShort = Simple(Subtitle().Replace(clipped, ""));
        return mine.Length > 0 && (mine == theirs || mine == theirsShort || Simple(title + subtitle) == theirs);
    }

    // "Le Guin, Ursula K." and "Ursula K. Le Guin" are the same author: the same words in any order.
    private static bool SameAuthor(string author, string clipped)
    {
        var mine = Words(author);
        var theirs = Words(clipped);
        return mine.Count > 0 && (mine.SetEquals(theirs) || (theirs.Count > 0 && theirs.IsSubsetOf(mine)));
    }

    private static HashSet<string> Words(string text) => Letters().Split(text.ToLowerInvariant()).Where(word => word.Length > 1).ToHashSet();

    private static string Simple(string? text) => Letters().Replace((text ?? "").ToLowerInvariant(), "");

    [GeneratedRegex(@"^(.*)\(([^()]*)\)\s*$")]
    private static partial Regex TitleAuthor();

    // A subtitle after a colon, or a series in brackets, which a Kindle often adds to the title.
    [GeneratedRegex(@"(:.*$|\s*\([^)]*\)\s*$)")]
    private static partial Regex Subtitle();

    [GeneratedRegex(@"highlight|markierung|subrayado|surlignement|evidenziazione|destaque", RegexOptions.IgnoreCase)]
    private static partial Regex HighlightWord();

    [GeneratedRegex(@"\bnot[ea]\b|notiz|\bnote\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoteWord();

    [GeneratedRegex(@"bookmark|lesezeichen|marcador|signet|segnalibro", RegexOptions.IgnoreCase)]
    private static partial Regex BookmarkWord();

    [GeneratedRegex(@"(?:page|seite|página|pagina)\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"(?:location|loc\.|position|posición|emplacement|posizione)\s+(\d+)(?:-(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex LocationNumbers();

    [GeneratedRegex(@"^\s*(added on|hinzugefügt am|añadido el|ajouté le|aggiunto il)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AddedWords();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Letters();
}
