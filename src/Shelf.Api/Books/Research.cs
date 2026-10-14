using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A question being looked into, across books: the reader's notes, each citing a book and where in it, gathered to
// read together and to export as a bibliography or a packet.
public sealed class ResearchTopic
{
    public const int MaxQuestionLength = 300;
    public const int MaxNotesLength = 4000;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public required string Question { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ResearchNote> Entries { get; set; } = [];
}

public sealed class ResearchNote
{
    public const int MaxTextLength = 4000;
    public const int MaxQuoteLength = 2000;
    public const int MaxPlaceLength = 40;
    public const int MaxLinkLength = 500;

    public int Id { get; set; }
    public int TopicId { get; set; }
    public ResearchTopic? Topic { get; set; }

    // The book it cites; kept as a note if the book goes.
    public int? BookId { get; set; }
    public Book? Book { get; set; }

    // Where in the book: a page, pages, a chapter, or a time in an audiobook, as the reader writes it.
    public string? Place { get; set; }
    public string? Quote { get; set; }
    public string? Text { get; set; }
    public string? Link { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum CitationStyle
{
    Chicago,
    Apa,
    Mla,
}

public sealed record ResearchTopicSummary(int Id, string Question, int Notes, int Books, DateTimeOffset UpdatedAt);

public sealed record ResearchNoteView(int Id, int? BookId, string? Title, string? Author, string? Place, string? Quote, string? Text, string? Link, DateTimeOffset CreatedAt);

public sealed record ResearchTopicView(int Id, string Question, string? Notes, ResearchNoteView[] Entries, string[] Bibliography);

public sealed record ResearchTopicRequest(string Question, string? Notes);

public sealed record ResearchNoteRequest(int? BookId, string? Place, string? Quote, string? Text, string? Link);

public static class Research
{
    public static async Task<ResearchTopicSummary[]> ListAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        [.. (await db.ResearchTopics.AsNoTracking()
                .Select(topic => new { topic.Id, topic.Question, topic.UpdatedAt, Notes = topic.Entries.Count, Books = topic.Entries.Where(entry => entry.BookId != null).Select(entry => entry.BookId).Distinct().Count() })
                .ToListAsync(cancellationToken))
            .OrderByDescending(topic => topic.UpdatedAt)
            .Select(topic => new ResearchTopicSummary(topic.Id, topic.Question, topic.Notes, topic.Books, topic.UpdatedAt))];

    public static async Task<ResearchTopicView?> GetAsync(ShelfDb db, int id, CitationStyle style = CitationStyle.Chicago, CancellationToken cancellationToken = default)
    {
        var topic = await db.ResearchTopics.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (topic is null)
        {
            return null;
        }

        var entries = await db.ResearchNotes.AsNoTracking().Where(note => note.TopicId == id).OrderBy(note => note.Id)
            .Select(note => new { note.Id, note.BookId, note.Book!.Title, note.Book.Author, note.Place, note.Quote, note.Text, note.Link, note.CreatedAt })
            .ToListAsync(cancellationToken);
        var cited = entries.Where(entry => entry.BookId is not null).Select(entry => entry.BookId!.Value).Distinct().ToList();
        var books = await db.Books.AsNoTracking().Where(book => cited.Contains(book.Id))
            .Select(book => new Book { Id = book.Id, Title = book.Title, Subtitle = book.Subtitle, Author = book.Author, Publisher = book.Publisher, Year = book.Year, Translator = book.Translator })
            .ToListAsync(cancellationToken);
        return new ResearchTopicView(topic.Id, topic.Question, topic.Notes,
            [.. entries.Select(entry => new ResearchNoteView(entry.Id, entry.BookId, entry.Title, entry.Author, entry.Place, entry.Quote, entry.Text, entry.Link, entry.CreatedAt))],
            Bibliography(books, style));
    }

    // The books cited, each once, in the style asked for, by author.
    public static string[] Bibliography(IEnumerable<Book> books, CitationStyle style) =>
        [.. books.OrderBy(book => SortName(book.Author), StringComparer.CurrentCultureIgnoreCase).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase).Select(book => Cite(book, style))];

    // A citation in plain text, the title set in *asterisks* as Markdown sets italics.
    public static string Cite(Book book, CitationStyle style)
    {
        var authors = book.Author.Split([" & ", " and ", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = authors.Length == 0 ? book.Author : authors[0];
        var title = book.Subtitle is { Length: > 0 } subtitle ? $"{book.Title}: {subtitle}" : book.Title;
        var year = book.Year?.ToString(CultureInfo.InvariantCulture);
        var publisher = string.IsNullOrWhiteSpace(book.Publisher) ? null : book.Publisher.Trim();
        string End(string text) => text.EndsWith('.') ? text : text + ".";
        switch (style)
        {
            case CitationStyle.Apa:
            {
                var names = string.Join(", ", authors.Select(Initials));
                var translated = book.Translator is { Length: > 0 } translator ? $" ({T("{0}, Trans.", translator)})" : "";
                return End($"{names} ({year ?? "n.d."}). *{title}*{translated}. {publisher ?? ""}".Trim());
            }

            case CitationStyle.Mla:
            {
                var names = authors.Length switch
                {
                    0 or 1 => Inverted(first),
                    2 => $"{Inverted(first)}, and {authors[1]}",
                    _ => $"{Inverted(first)}, et al",
                };
                var tail = string.Join(", ", new[] { publisher, year }.OfType<string>());
                return End($"{End(names)} *{title}*." + (tail.Length > 0 ? $" {tail}" : ""));
            }

            default:
            {
                var names = authors.Length <= 1 ? Inverted(first) : $"{Inverted(first)}, " + string.Join(", ", authors.Skip(1).SkipLast(1)) + (authors.Length > 2 ? ", " : "") + $"and {authors[^1]}";
                var translated = book.Translator is { Length: > 0 } translator ? $" {T("Translated by {0}.", translator)}" : "";
                var tail = string.Join(", ", new[] { publisher, year }.OfType<string>());
                return End($"{End(names)} *{title}*.{translated}" + (tail.Length > 0 ? $" {tail}" : ""));
            }
        }
    }

    // "Ursula K. Le Guin" as "Le Guin, Ursula K."; a name already written last-first, or a single word, as it is.
    private static string Inverted(string name)
    {
        if (name.Contains(',') || !name.Contains(' '))
        {
            return name.Trim();
        }

        var (given, family) = Split(name);
        return $"{family}, {given}";
    }

    // "Ursula K. Le Guin" as "Le Guin, U. K."
    private static string Initials(string name)
    {
        var (given, family) = name.Contains(',') ? (name[(name.IndexOf(',') + 1)..].Trim(), name[..name.IndexOf(',')].Trim()) : Split(name);
        var initials = string.Join(" ", given.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => part.TrimEnd('.')).Where(part => part.Length > 0).Select(part => char.ToUpperInvariant(part[0]) + "."));
        return initials.Length == 0 ? family : $"{family}, {initials}";
    }

    // The family name is the last word, with the particles that go before it (Le Guin, van Gogh, de la Fontaine).
    private static (string Given, string Family) Split(string name)
    {
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 1)
        {
            return ("", parts[0]);
        }

        var at = parts.Count - 1;
        while (at > 1 && Particles.Contains(parts[at - 1].ToLowerInvariant()))
        {
            at--;
        }

        return (string.Join(' ', parts.Take(at)), string.Join(' ', parts.Skip(at)));
    }

    private static readonly HashSet<string> Particles = ["le", "la", "de", "du", "van", "von", "der", "den", "da", "di", "del", "della", "des", "ter", "ten", "bin", "al"];

    private static string SortName(string author) => Inverted(author.Split([" & ", " and ", ";"], StringSplitOptions.TrimEntries).FirstOrDefault() ?? author);

    // The question, the reader's thoughts, every note grouped by the book it cites with where in it, and the
    // bibliography: a packet to read on paper or take elsewhere.
    public static string Packet(ResearchTopicView topic, CitationStyle style)
    {
        var text = new StringBuilder();
        text.Append("# ").AppendLine(topic.Question).AppendLine();
        if (!string.IsNullOrWhiteSpace(topic.Notes))
        {
            text.AppendLine(topic.Notes.Trim()).AppendLine();
        }

        foreach (var group in topic.Entries.GroupBy(entry => (entry.BookId, entry.Title, entry.Author)).OrderBy(group => group.Key.BookId is null ? 1 : 0).ThenBy(group => group.Key.Title))
        {
            text.Append("## ").AppendLine(group.Key.Title is { } title ? $"{title}, {group.Key.Author}" : T("Other notes")).AppendLine();
            foreach (var entry in group)
            {
                if (entry.Quote is { Length: > 0 } quote)
                {
                    text.AppendLine(string.Join('\n', quote.Trim().Split('\n').Select(line => "> " + line)));
                    if (entry.Place is { Length: > 0 } place)
                    {
                        text.Append("> — ").AppendLine(place);
                    }

                    text.AppendLine();
                }
                else if (entry.Place is { Length: > 0 } place)
                {
                    text.Append('*').Append(place).AppendLine("*").AppendLine();
                }

                if (entry.Text is { Length: > 0 } note)
                {
                    text.AppendLine(note.Trim()).AppendLine();
                }

                if (entry.Link is { Length: > 0 } link)
                {
                    text.Append('<').Append(link).AppendLine(">").AppendLine();
                }
            }
        }

        if (topic.Bibliography.Length > 0)
        {
            text.Append("## ").AppendLine(T("Bibliography")).AppendLine();
            foreach (var entry in topic.Bibliography)
            {
                text.Append("- ").AppendLine(entry);
            }
        }

        return text.ToString();
    }

    public static Dictionary<string, string[]>? ValidateTopic(ResearchTopicRequest request) =>
        string.IsNullOrWhiteSpace(request.Question) || request.Question.Trim().Length > ResearchTopic.MaxQuestionLength
            ? new() { ["Question"] = [T("Write the question, in up to {0} characters.", ResearchTopic.MaxQuestionLength)] }
            : request.Notes is { Length: > ResearchTopic.MaxNotesLength } ? new() { ["Notes"] = [T("Notes can be up to {0} characters.", ResearchTopic.MaxNotesLength)] } : null;

    public static Dictionary<string, string[]>? ValidateNote(ResearchNoteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text) && string.IsNullOrWhiteSpace(request.Quote) && string.IsNullOrWhiteSpace(request.Link))
        {
            return new() { ["Text"] = [T("Write a note, a quotation, or a link.")] };
        }

        if (request.Text is { Length: > ResearchNote.MaxTextLength } || request.Quote is { Length: > ResearchNote.MaxQuoteLength } || request.Place is { Length: > ResearchNote.MaxPlaceLength } || request.Link is { Length: > ResearchNote.MaxLinkLength })
        {
            return new() { ["Text"] = [T("That is longer than a note can be.")] };
        }

        if (request.Link is { Length: > 0 } link && !(Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            return new() { ["Link"] = [T("A link starts with http:// or https://.")] };
        }

        return null;
    }

    public static async Task<ResearchTopic> CreateAsync(ShelfDb db, ResearchTopicRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var topic = new ResearchTopic { OwnerId = db.ReaderId, Question = request.Question.Trim(), Notes = Blank(request.Notes), CreatedAt = now, UpdatedAt = now };
        db.ResearchTopics.Add(topic);
        await db.SaveChangesAsync(cancellationToken);
        return topic;
    }

    public static async Task<bool> UpdateAsync(ShelfDb db, int id, ResearchTopicRequest request, CancellationToken cancellationToken = default)
    {
        var topic = await db.ResearchTopics.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (topic is null)
        {
            return false;
        }

        (topic.Question, topic.Notes, topic.UpdatedAt) = (request.Question.Trim(), Blank(request.Notes), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // A note on a topic of the reader's own, citing one of their own books if any.
    public static async Task<ResearchNote?> AddNoteAsync(ShelfDb db, int topicId, ResearchNoteRequest request, CancellationToken cancellationToken = default)
    {
        var topic = await db.ResearchTopics.FirstOrDefaultAsync(item => item.Id == topicId, cancellationToken);
        if (topic is null || (request.BookId is int bookId && !await db.Books.AnyAsync(book => book.Id == bookId, cancellationToken)))
        {
            return null;
        }

        var note = new ResearchNote
        {
            TopicId = topicId, BookId = request.BookId, Place = Blank(request.Place), Quote = Blank(request.Quote), Text = Blank(request.Text),
            Link = Blank(request.Link), CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ResearchNotes.Add(note);
        topic.UpdatedAt = note.CreatedAt;
        await db.SaveChangesAsync(cancellationToken);
        return note;
    }

    // A quote kept on a book, brought to a topic with its page.
    public static async Task<ResearchNote?> AddQuoteAsync(ShelfDb db, int topicId, int quoteId, CancellationToken cancellationToken = default)
    {
        var quote = await db.Quotes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == quoteId, cancellationToken);
        return quote is null
            ? null
            : await AddNoteAsync(db, topicId, new ResearchNoteRequest(quote.BookId, quote.Page is int page ? T("p. {0}", page) : null, quote.Text, null, null), cancellationToken);
    }

    public static async Task<bool> UpdateNoteAsync(ShelfDb db, int topicId, int noteId, ResearchNoteRequest request, CancellationToken cancellationToken = default)
    {
        var note = await db.ResearchNotes.FirstOrDefaultAsync(item => item.Id == noteId && item.TopicId == topicId, cancellationToken);
        if (note is null || (request.BookId is int bookId && !await db.Books.AnyAsync(book => book.Id == bookId, cancellationToken)))
        {
            return false;
        }

        (note.BookId, note.Place, note.Quote, note.Text, note.Link) = (request.BookId, Blank(request.Place), Blank(request.Quote), Blank(request.Text), Blank(request.Link));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Notes the reader has made citing a book, with the question each is under.
    public static async Task<(int TopicId, string Question, ResearchNoteView Note)[]> ForBookAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default) =>
        [.. (await db.ResearchNotes.AsNoTracking().Where(note => note.BookId == bookId).OrderByDescending(note => note.Id)
                .Select(note => new { note.TopicId, note.Topic!.Question, note.Id, note.Place, note.Quote, note.Text, note.Link, note.CreatedAt })
                .ToListAsync(cancellationToken))
            .Select(note => (note.TopicId, note.Question, new ResearchNoteView(note.Id, bookId, null, null, note.Place, note.Quote, note.Text, note.Link, note.CreatedAt)))];

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public static void Map(RouteGroupBuilder books)
    {
        var research = books.MapGroup("/research").WithTags("Research");
        research.MapGet("/", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ListAsync(db, cancellationToken)));
        research.MapPost("/", async Task<Results<Created<ResearchTopicSummary>, ValidationProblem>> (ResearchTopicRequest request, ShelfDb db, CancellationToken cancellationToken) =>
        {
            if (ValidateTopic(request) is { } problems)
            {
                return TypedResults.ValidationProblem(problems);
            }

            var topic = await CreateAsync(db, request, cancellationToken);
            return TypedResults.Created($"/books/research/{topic.Id}", new ResearchTopicSummary(topic.Id, topic.Question, 0, 0, topic.UpdatedAt));
        });
        research.MapGet("/{id:int}", async Task<Results<Ok<ResearchTopicView>, NotFound>> (int id, CitationStyle? style, ShelfDb db, CancellationToken cancellationToken) =>
            await GetAsync(db, id, style ?? CitationStyle.Chicago, cancellationToken) is { } topic ? TypedResults.Ok(topic) : TypedResults.NotFound());
        research.MapPut("/{id:int}", async Task<Results<NoContent, NotFound, ValidationProblem>> (int id, ResearchTopicRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            ValidateTopic(request) is { } problems ? TypedResults.ValidationProblem(problems)
            : await UpdateAsync(db, id, request, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        research.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await db.ResearchTopics.Where(topic => topic.Id == id).ExecuteDeleteAsync(cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound());
        research.MapGet("/{id:int}/packet.md", async Task<IResult> (int id, CitationStyle? style, ShelfDb db, CancellationToken cancellationToken) =>
            await GetAsync(db, id, style ?? CitationStyle.Chicago, cancellationToken) is { } topic
                ? Results.Text(Packet(topic, style ?? CitationStyle.Chicago), "text/markdown; charset=utf-8")
                : TypedResults.NotFound());
        research.MapPost("/{id:int}/notes", async Task<Results<Ok<int>, NotFound, ValidationProblem>> (int id, ResearchNoteRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            ValidateNote(request) is { } problems ? TypedResults.ValidationProblem(problems)
            : await AddNoteAsync(db, id, request, cancellationToken) is { } note ? TypedResults.Ok(note.Id) : TypedResults.NotFound());
        research.MapPost("/{id:int}/quotes/{quoteId:int}", async Task<Results<Ok<int>, NotFound>> (int id, int quoteId, ShelfDb db, CancellationToken cancellationToken) =>
            await AddQuoteAsync(db, id, quoteId, cancellationToken) is { } note ? TypedResults.Ok(note.Id) : TypedResults.NotFound());
        research.MapPut("/{id:int}/notes/{noteId:int}", async Task<Results<NoContent, NotFound, ValidationProblem>> (int id, int noteId, ResearchNoteRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            ValidateNote(request) is { } problems ? TypedResults.ValidationProblem(problems)
            : await UpdateNoteAsync(db, id, noteId, request, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        research.MapDelete("/{id:int}/notes/{noteId:int}", async Task<Results<NoContent, NotFound>> (int id, int noteId, ShelfDb db, CancellationToken cancellationToken) =>
            await db.ResearchNotes.Where(note => note.Id == noteId && note.TopicId == id).ExecuteDeleteAsync(cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound());
    }
}
