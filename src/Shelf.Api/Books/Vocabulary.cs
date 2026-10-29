using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A word or phrase a reader keeps while reading in another language: what it means, the sentence it was met in, and
// how well it is known, for reviewing a little at a time.
public sealed class VocabularyWord
{
    public const int MaxWordLength = 100;
    public const int MaxMeaningLength = 1000;
    public const int MaxContextLength = 1000;
    public const int MaxNoteLength = 500;
    public const int MaxLanguageLength = 40;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public int? BookId { get; set; }
    public Book? Book { get; set; }
    public required string Word { get; set; }
    public required string Language { get; set; }
    public string? Meaning { get; set; }
    public string? Context { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset AddedAt { get; set; }

    // How well it is known, 0 to 5; reviewed again on the day due. At 5 it is learned.
    public int Box { get; set; }
    public DateOnly DueOn { get; set; }
}

// A passage beside its translation: the same lines in two languages, from one edition or two.
public sealed class ParallelPassage
{
    public const int MaxTextLength = 4000;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public int? BookId { get; set; }
    public Book? Book { get; set; }
    public int? OtherBookId { get; set; }
    public required string Original { get; set; }
    public string? OriginalLanguage { get; set; }
    public required string Translation { get; set; }
    public string? TranslationLanguage { get; set; }
    public string? Place { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record WordView(int Id, string Word, string Language, string? Meaning, string? Context, string? Note, int? BookId, string? Title, int Box, DateOnly DueOn, bool Learned);

public sealed record WordRequest(string Word, string? Language = null, string? Meaning = null, string? Context = null, string? Note = null, int? BookId = null);

public sealed record ReviewRequest(bool Knew);

public sealed record LanguageCount(string Language, int Words, int Learned, int Due);

public sealed record PassageView(int Id, int? BookId, string? Title, int? OtherBookId, string? OtherTitle, string Original, string? OriginalLanguage, string Translation, string? TranslationLanguage, string? Place, string? Note);

public sealed record PassageRequest(string Original, string Translation, int? BookId = null, int? OtherBookId = null, string? OriginalLanguage = null, string? TranslationLanguage = null, string? Place = null, string? Note = null);

public static class Vocabulary
{
    public const int Learned = 5;

    // Days until the next review after knowing a word, by how well it is known.
    private static readonly int[] Intervals = [1, 2, 4, 9, 21, 60];

    private static DateOnly Today(ShelfDb db) => db.Today;

    public static Dictionary<string, string[]>? Validate(WordRequest request) =>
        string.IsNullOrWhiteSpace(request.Word) || request.Word.Trim().Length > VocabularyWord.MaxWordLength
            ? new() { ["Word"] = [T("Write the word, in up to {0} characters.", VocabularyWord.MaxWordLength)] }
            : request.Meaning is { Length: > VocabularyWord.MaxMeaningLength } || request.Context is { Length: > VocabularyWord.MaxContextLength } || request.Note is { Length: > VocabularyWord.MaxNoteLength } || request.Language is { Length: > VocabularyWord.MaxLanguageLength }
                ? new() { ["Word"] = [T("That is longer than a word can keep.")] }
                : null;

    // Kept once for each language: keeping it again adds what is new to it, and leaves how well it is known.
    public static async Task<VocabularyWord?> KeepAsync(ShelfDb db, WordRequest request, CancellationToken cancellationToken = default)
    {
        string? language = Blank(request.Language);
        if (request.BookId is int bookId)
        {
            var book = await db.Books.AsNoTracking().Where(item => item.Id == bookId).Select(item => new { item.Language }).FirstOrDefaultAsync(cancellationToken);
            if (book is null)
            {
                return null;
            }

            language ??= Blank(book.Language);
        }

        language = (language ?? T("Unknown")).Trim();
        var word = request.Word.Trim();
        var kept = await db.VocabularyWords.FirstOrDefaultAsync(item => item.Word == word && item.Language == language, cancellationToken);
        if (kept is null)
        {
            kept = new VocabularyWord { OwnerId = db.ReaderId, Word = word, Language = language, AddedAt = DateTimeOffset.UtcNow, DueOn = Today(db) };
            db.VocabularyWords.Add(kept);
        }

        kept.Meaning = Blank(request.Meaning) ?? kept.Meaning;
        kept.Context = Blank(request.Context) ?? kept.Context;
        kept.Note = Blank(request.Note) ?? kept.Note;
        kept.BookId ??= request.BookId;
        await db.SaveChangesAsync(cancellationToken);
        return kept;
    }

    public static async Task<bool> UpdateAsync(ShelfDb db, int id, WordRequest request, CancellationToken cancellationToken = default)
    {
        var word = await db.VocabularyWords.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (word is null)
        {
            return false;
        }

        (word.Word, word.Meaning, word.Context, word.Note) = (request.Word.Trim(), Blank(request.Meaning), Blank(request.Context), Blank(request.Note));
        word.Language = Blank(request.Language) ?? word.Language;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Known: further on, and further off; not known: back to the start, to see again tomorrow.
    public static async Task<WordView?> ReviewAsync(ShelfDb db, int id, bool knew, CancellationToken cancellationToken = default)
    {
        var word = await db.VocabularyWords.Include(item => item.Book).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (word is null)
        {
            return null;
        }

        word.Box = knew ? Math.Min(Learned, word.Box + 1) : 0;
        word.DueOn = Today(db).AddDays(knew ? Intervals[word.Box] : 1);
        await db.SaveChangesAsync(cancellationToken);
        return View(word, word.Book?.Title);
    }

    public static async Task<WordView[]> ListAsync(ShelfDb db, string? language, int? bookId, bool dueOnly, CancellationToken cancellationToken = default)
    {
        var query = db.VocabularyWords.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(language))
        {
            query = query.Where(word => word.Language == language);
        }

        if (bookId is int id)
        {
            query = query.Where(word => word.BookId == id);
        }

        if (dueOnly)
        {
            var today = Today(db);
            query = query.Where(word => word.Box < Learned && word.DueOn <= today).OrderBy(word => word.DueOn).ThenBy(word => word.Box);
        }
        else
        {
            query = query.OrderBy(word => word.Word);
        }

        return [.. (await query.Select(word => new { Word = word, word.Book!.Title }).Take(2000).ToListAsync(cancellationToken)).Select(item => View(item.Word, item.Title))];
    }

    public static async Task<LanguageCount[]> LanguagesAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var today = Today(db);
        return [.. (await db.VocabularyWords.AsNoTracking()
                .GroupBy(word => word.Language)
                .Select(group => new { group.Key, Words = group.Count(), Learned = group.Count(word => word.Box >= Learned), Due = group.Count(word => word.Box < Learned && word.DueOn <= today) })
                .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.Words)
            .Select(item => new LanguageCount(item.Key, item.Words, item.Learned, item.Due))];
    }

    // For Anki or any flashcards: the word, then its meaning with the sentence it came from; tab-separated.
    public static string Cards(IEnumerable<WordView> words)
    {
        static string Clean(string? text) => (text ?? "").Replace('\t', ' ').Replace("\r", "").Replace("\n", "<br>");
        var text = new StringBuilder("#separator:tab\n#html:true\n#columns:Word\tMeaning\tContext\tLanguage\n");
        foreach (var word in words)
        {
            text.Append(Clean(word.Word)).Append('\t').Append(Clean(word.Meaning)).Append('\t').Append(Clean(word.Context)).Append('\t').Append(Clean(word.Language)).Append('\n');
        }

        return text.ToString();
    }

    public static WordView View(VocabularyWord word, string? title) =>
        new(word.Id, word.Word, word.Language, word.Meaning, word.Context, word.Note, word.BookId, title, word.Box, word.DueOn, word.Box >= Learned);

    public static Dictionary<string, string[]>? Validate(PassageRequest request) =>
        string.IsNullOrWhiteSpace(request.Original) || string.IsNullOrWhiteSpace(request.Translation)
            ? new() { ["Original"] = [T("Write the passage and its translation.")] }
            : request.Original.Length > ParallelPassage.MaxTextLength || request.Translation.Length > ParallelPassage.MaxTextLength || request.Note is { Length: > 1000 } || request.Place is { Length: > 40 }
                ? new() { ["Original"] = [T("A passage can be up to {0} characters.", ParallelPassage.MaxTextLength)] }
                : null;

    public static async Task<ParallelPassage?> AddPassageAsync(ShelfDb db, PassageRequest request, CancellationToken cancellationToken = default)
    {
        foreach (var id in new[] { request.BookId, request.OtherBookId }.OfType<int>())
        {
            if (!await db.Books.AnyAsync(book => book.Id == id, cancellationToken))
            {
                return null;
            }
        }

        var passage = new ParallelPassage
        {
            OwnerId = db.ReaderId, BookId = request.BookId, OtherBookId = request.OtherBookId, Original = request.Original.Trim(), Translation = request.Translation.Trim(),
            OriginalLanguage = Blank(request.OriginalLanguage), TranslationLanguage = Blank(request.TranslationLanguage), Place = Blank(request.Place), Note = Blank(request.Note),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ParallelPassages.Add(passage);
        await db.SaveChangesAsync(cancellationToken);
        return passage;
    }

    public static async Task<PassageView[]> PassagesAsync(ShelfDb db, int? bookId, CancellationToken cancellationToken = default)
    {
        var rows = await db.ParallelPassages.AsNoTracking()
            .Where(passage => bookId == null || passage.BookId == bookId || passage.OtherBookId == bookId)
            .OrderByDescending(passage => passage.Id)
            .Select(passage => new { Passage = passage, passage.Book!.Title })
            .ToListAsync(cancellationToken);
        var others = rows.Select(row => row.Passage.OtherBookId).OfType<int>().Distinct().ToList();
        var titles = await db.Books.AsNoTracking().Where(book => others.Contains(book.Id)).ToDictionaryAsync(book => book.Id, book => book.Title, cancellationToken);
        return [.. rows.Select(row => new PassageView(row.Passage.Id, row.Passage.BookId, row.Title, row.Passage.OtherBookId,
            row.Passage.OtherBookId is int other ? titles.GetValueOrDefault(other) : null, row.Passage.Original, row.Passage.OriginalLanguage,
            row.Passage.Translation, row.Passage.TranslationLanguage, row.Passage.Place, row.Passage.Note))];
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public static void Map(RouteGroupBuilder books)
    {
        var words = books.MapGroup("/words").WithTags("Languages");
        words.MapGet("/", async (string? language, int? bookId, bool? due, ShelfDb db, CancellationToken cancellationToken) =>
            TypedResults.Ok(await ListAsync(db, language, bookId, due == true, cancellationToken)));
        words.MapGet("/languages", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await LanguagesAsync(db, cancellationToken)));
        words.MapGet("/cards.txt", async (string? language, ShelfDb db, CancellationToken cancellationToken) =>
            Results.Text(Cards(await ListAsync(db, language, null, false, cancellationToken)), "text/plain; charset=utf-8"));
        words.MapPost("/", async Task<Results<Ok<WordView>, NotFound, ValidationProblem>> (WordRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            Validate(request) is { } problems ? TypedResults.ValidationProblem(problems)
            : await KeepAsync(db, request, cancellationToken) is { } word ? TypedResults.Ok(View(word, null)) : TypedResults.NotFound());
        words.MapPut("/{id:int}", async Task<Results<NoContent, NotFound, ValidationProblem>> (int id, WordRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            Validate(request) is { } problems ? TypedResults.ValidationProblem(problems)
            : await UpdateAsync(db, id, request, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        words.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await db.VocabularyWords.Where(word => word.Id == id).ExecuteDeleteAsync(cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound());
        words.MapPost("/{id:int}/review", async Task<Results<Ok<WordView>, NotFound>> (int id, ReviewRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await ReviewAsync(db, id, request.Knew, cancellationToken) is { } word ? TypedResults.Ok(word) : TypedResults.NotFound());

        var passages = books.MapGroup("/passages").WithTags("Languages");
        passages.MapGet("/", async (int? bookId, ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await PassagesAsync(db, bookId, cancellationToken)));
        passages.MapPost("/", async Task<Results<Ok<int>, NotFound, ValidationProblem>> (PassageRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            Validate(request) is { } problems ? TypedResults.ValidationProblem(problems)
            : await AddPassageAsync(db, request, cancellationToken) is { } passage ? TypedResults.Ok(passage.Id) : TypedResults.NotFound());
        passages.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await db.ParallelPassages.Where(passage => passage.Id == id).ExecuteDeleteAsync(cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound());
    }
}
