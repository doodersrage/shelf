using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A summary of one part of a book, from that part's text alone, kept so it is written once.
public sealed class PartSummary
{
    public int Id { get; set; }
    public int BookId { get; set; }

    // The file it was written from: the e-book's stored name, or the recording's for a transcript.
    public required string Source { get; set; }
    public int Part { get; set; }
    public required string Language { get; set; }
    public required string Text { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record RecapRequest(int? Chapter = null, double? At = null, double? Seconds = null);

// The recap, and how much of the book it covers.
public sealed record Recap(string Text, int Parts, bool Partly);

public interface IRecapWriter
{
    bool Ready { get; }

    // What the model wrote; null when it could not be asked.
    Task<string?> WriteAsync(string system, string prompt, int most, CancellationToken cancellationToken);
}

// Anthropic's Messages API, with the key an admin gives (Recap:ApiKey) and the model they choose (Recap:Model).
public sealed class AnthropicWriter(HttpClient http, IConfiguration configuration) : IRecapWriter
{
    public bool Ready => !string.IsNullOrWhiteSpace(configuration["Recap:ApiKey"]);

    public async Task<string?> WriteAsync(string system, string prompt, int most, CancellationToken cancellationToken)
    {
        if (!Ready)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, configuration["Recap:Url"] ?? "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = configuration["Recap:Model"] ?? "claude-haiku-5-5",
                max_tokens = most,
                system,
                messages = new[] { new { role = "user", content = prompt } },
            }),
        };
        request.Headers.Add("x-api-key", configuration["Recap:ApiKey"]);
        request.Headers.Add("anthropic-version", "2023-06-01");
        try
        {
            using var answer = await http.SendAsync(request, cancellationToken);
            if (!answer.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await answer.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return json.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? string.Concat(content.EnumerateArray().Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "text").Select(part => part.GetProperty("text").GetString())).Trim() is { Length: > 0 } text ? text : null
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}

// "Previously in this book": what has happened up to the reader's place, and nothing after it. Each part before the
// place is summed up from its own text and kept; the recap is written from those and the latest passage read.
public sealed class Recaps(IRecapWriter writer, EbookStore ebooks, IConfiguration configuration)
{
    // A part's text is cut to this many characters before it is sent: about 15,000 words.
    public const int MostPartCharacters = 90_000;
    private const int LatestCharacters = 4_000;

    public bool Enabled => writer.Ready && configuration.GetValue("Recap:Enabled", true);

    public static void Map(RouteGroupBuilder books) =>
        books.MapPost("/{id:int}/recap", async Task<Results<Ok<Recap>, NotFound, ValidationProblem>> (int id, RecapRequest request, ShelfDb db, Recaps recaps, CancellationToken cancellationToken) =>
            await recaps.WriteAsync(db, id, request, cancellationToken) switch
            {
                (Recap recap, _) => TypedResults.Ok(recap),
                (null, "") => TypedResults.NotFound(),
                (null, var problem) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["recap"] = [problem!] }),
            });

    public async Task<(Recap? Recap, string? Problem)> WriteAsync(ShelfDb db, int id, RecapRequest request, CancellationToken cancellationToken = default)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open)
        {
            return (null, "");
        }

        if (!Enabled)
        {
            return (null, T("This shelf has no recaps set up. An admin can add a key in Recap:ApiKey."));
        }

        var book = open.Book;
        var languageName = Current switch { "es" => "Spanish", "fr" => "French", "de" => "German", _ => "English" };
        List<string> parts;
        string source;
        int upTo;
        double within;
        if (request.Seconds is double seconds)
        {
            // An audiobook with a transcript: ten-minute parts, up to the moment reached.
            var spoken = await db.SpokenTexts.AsNoTracking().Where(text => text.BookId == id && text.AudioStoredName == book.AudioStoredName).Select(text => new { text.Start, text.Text }).ToListAsync(cancellationToken);
            if (spoken.Count == 0)
            {
                return (null, T("This audiobook has no transcript yet to recap from."));
            }

            var ordered = spoken.OrderBy(text => text.Start).ToList();
            parts = ordered.Where(text => text.Start <= seconds).GroupBy(text => (int)(text.Start / 600)).OrderBy(group => group.Key).Select(group => string.Join(' ', group.Select(text => text.Text))).ToList();
            (source, upTo, within) = (book.AudioStoredName!, Math.Max(0, parts.Count - 1), 1);
        }
        else if (ebooks.OpenPath(book.EbookStoredName) is { } epub && EbookStore.IsEpub(book.EbookStoredName))
        {
            var count = EpubFile.Chapters(epub)?.Count ?? 0;
            upTo = Math.Clamp(request.Chapter ?? 0, 0, Math.Max(0, count - 1));
            parts = Enumerable.Range(0, upTo + 1).Select(index => EpubFile.ChapterHtml(epub, index, "") is { } html ? Search.PlainText(html) : "").ToList();
            (source, within) = (book.EbookStoredName!, Math.Clamp(request.At ?? 0, 0, 1));
        }
        else
        {
            return (null, T("Recaps need an EPUB, or an audiobook with a transcript."));
        }

        if (parts.All(string.IsNullOrWhiteSpace))
        {
            return (null, T("There is nothing read yet to recap."));
        }

        // The parts before the place, each summed up once from its own text.
        var kept = await db.PartSummaries.Where(summary => summary.BookId == id && summary.Source == source && summary.Language == Current).ToDictionaryAsync(summary => summary.Part, cancellationToken);
        var summaries = new List<(int Part, string Text)>();
        for (var part = 0; part < upTo; part++)
        {
            if (string.IsNullOrWhiteSpace(parts[part]) || parts[part].Length < 200)
            {
                continue;
            }

            if (!kept.TryGetValue(part, out var summary))
            {
                var text = parts[part].Length > MostPartCharacters ? parts[part][..MostPartCharacters] : parts[part];
                var written = await writer.WriteAsync(
                    $"You sum up one part of a book for someone who has just read it. In {languageName}, write three to six plain sentences in the past tense: who appears, what happens, and anything revealed. Use only this part's text; never guess at what comes later.",
                    $"From {book.Title} by {book.Author}, part {part + 1}:\n\n{text}",
                    400,
                    cancellationToken);
                if (written is null)
                {
                    return (null, T("The recap could not be written just now. Try again in a while."));
                }

                summary = new PartSummary { BookId = id, Source = source, Part = part, Language = Current, Text = written, CreatedAt = DateTimeOffset.UtcNow };
                db.PartSummaries.Add(summary);
                await db.SaveChangesAsync(cancellationToken);
            }

            summaries.Add((part, summary.Text));
        }

        // The part the reader is in, up to their place: its latest passage, word for word.
        var current = parts[upTo];
        var reached = current[..(int)Math.Round(current.Length * within)];
        var latest = reached.Length > LatestCharacters ? reached[^LatestCharacters..] : reached;
        var prompt = new StringBuilder($"The book is {book.Title} by {book.Author}.\n\n");
        foreach (var (part, text) in summaries)
        {
            prompt.Append($"Part {part + 1}: {text}\n\n");
        }

        prompt.Append(latest.Length > 0 ? $"The reader stopped here, in part {upTo + 1}; the last passage they read:\n\n{latest}" : $"The reader is at the start of part {upTo + 1}.");
        var recap = await writer.WriteAsync(
            $"You write a short \"previously\" for a reader coming back to a book after a break. In {languageName}, in two or three short paragraphs, remind them who is who and what has happened, ending with where they stopped. Use only what you are given: it covers the book up to their place, so never mention, hint at, or guess what happens next.",
            prompt.ToString(),
            700,
            cancellationToken);
        return recap is null ? (null, T("The recap could not be written just now. Try again in a while.")) : (new Recap(recap, upTo + 1, within < 1), null);
    }
}
