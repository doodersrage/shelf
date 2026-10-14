using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// Which column of a spreadsheet holds each detail of a book, what its words for a status mean, and how its dates
// and tags are written: worked out once for a service's export, and kept to use again.
public sealed record RecipeSpec(
    Dictionary<string, string> Columns,
    Dictionary<string, BookStatus>? Statuses = null,
    string? TagSeparator = null,
    string? DateFormat = null,
    bool RatingOutOfTen = false);

public sealed class ImportRecipe
{
    public const int MaxNameLength = 80;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public required string Name { get; set; }

    // The RecipeSpec, as JSON.
    public required string Spec { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed record RecipeView(int Id, string Name, RecipeSpec Spec, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record RecipeRow(int Line, string? Title, string? Author, BookStatus? Status, string? Problem);

// What a file holds and how it would come in: its columns, a guessed recipe, the words in its status column, and
// the first rows read with the recipe.
public sealed record RecipePreview(string[] Columns, RecipeSpec Spec, string[] StatusWords, RecipeRow[] Rows, int Total);

public sealed record RecipeImportResult(int Added, int Skipped, int? RecipeId, int? BatchId);

public static class ImportRecipes
{
    // The details a recipe can fill, in the order the page lists them.
    public static readonly IReadOnlyList<string> Fields =
    [
        "Title", "Author", "Isbn", "Status", "Rating", "Year", "Pages", "Publisher", "Language", "Format", "Subtitle", "Series", "SeriesNumber",
        "Tags", "Notes", "Review", "StartedOn", "FinishedOn", "AddedAt", "Location", "Translator", "Narrator",
    ];

    public static readonly IReadOnlyList<string> DateFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "dd/MM/yyyy", "dd.MM.yyyy", "d MMM yyyy", "MMM d, yyyy"];

    public const int PreviewRows = 10;
    public const long MaxBytes = 20 * 1024 * 1024;

    // The names services give each column, lower case without spaces or punctuation.
    private static readonly Dictionary<string, string[]> Synonyms = new()
    {
        ["Title"] = ["title", "booktitle", "name", "titel", "titre", "titulo"],
        ["Author"] = ["author", "authors", "writer", "by", "creator", "autor", "auteur", "authorname", "primaryauthor"],
        ["Isbn"] = ["isbn", "isbn13", "isbn10", "ean", "isbnuid", "barcode"],
        ["Status"] = ["status", "readstatus", "shelf", "exclusiveshelf", "readingstatus", "state"],
        ["Rating"] = ["rating", "myrating", "stars", "starrating", "score"],
        ["Year"] = ["year", "published", "publicationyear", "yearpublished", "originalpublicationyear", "pubyear"],
        ["Pages"] = ["pages", "pagecount", "numberofpages", "length"],
        ["Publisher"] = ["publisher", "verlag", "editeur", "editorial"],
        ["Language"] = ["language", "lang", "sprache", "langue", "idioma"],
        ["Format"] = ["format", "binding", "type", "media"],
        ["Subtitle"] = ["subtitle"],
        ["Series"] = ["series", "seriesname", "reihe"],
        ["SeriesNumber"] = ["seriesnumber", "seriesindex", "volume", "numberinseries", "seriesposition"],
        ["Tags"] = ["tags", "bookshelves", "genres", "genre", "subjects", "categories", "labels", "collections"],
        ["Notes"] = ["notes", "privatenotes", "comments", "note"],
        ["Review"] = ["review", "myreview"],
        ["StartedOn"] = ["started", "datestarted", "startdate", "startedon", "startedreading"],
        ["FinishedOn"] = ["finished", "dateread", "datefinished", "finishdate", "lastdateread", "finishedon", "read"],
        ["AddedAt"] = ["added", "dateadded", "addedon", "created", "dateacquired"],
        ["Location"] = ["location", "shelflocation", "place", "room"],
        ["Translator"] = ["translator", "translatedby"],
        ["Narrator"] = ["narrator", "narratedby", "reader"],
    };

    private static string Plain(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    // A recipe guessed from a file's header: each detail to the first column whose name says it.
    public static RecipeSpec Guess(IReadOnlyList<string> header)
    {
        var columns = new Dictionary<string, string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            foreach (var name in Synonyms[field])
            {
                if (header.FirstOrDefault(column => Plain(column) == name && !taken.Contains(column)) is { } found)
                {
                    columns[field] = found;
                    taken.Add(found);
                    break;
                }
            }
        }

        return new RecipeSpec(columns, TagSeparator: header.Any(column => Plain(column) == "bookshelves") ? "," : null);
    }

    // What a word in a status column most likely means, when the recipe does not say.
    public static BookStatus? StatusOf(string? word) => Plain(word ?? "") switch
    {
        "" => null,
        "read" or "finished" or "done" or "completed" or "gelesen" or "lu" or "leido" => BookStatus.Finished,
        "reading" or "currentlyreading" or "current" or "inprogress" or "started" => BookStatus.Reading,
        "toread" or "want" or "wanttoread" or "tbr" or "unread" or "planned" or "wishlist" => BookStatus.Want,
        "dnf" or "didnotfinish" or "abandoned" or "stopped" => BookStatus.Abandoned,
        var other => Enum.TryParse<BookStatus>(other, ignoreCase: true, out var status) ? status : null,
    };

    // The rows of a file made books by a recipe. A row without a title and an author is skipped, with why.
    public static (List<CsvBook> Books, List<RecipeRow> Rows) Read(List<string[]> rows, RecipeSpec spec)
    {
        var header = rows.Count == 0 ? [] : rows[0].Select(name => name.Trim()).ToList();
        var index = spec.Columns.Where(pair => Fields.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => header.FindIndex(column => column.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)));
        var statuses = (spec.Statuses ?? []).ToDictionary(pair => Plain(pair.Key), pair => pair.Value);
        var books = new List<CsvBook>();
        var seen = new List<RecipeRow>();
        for (var line = 1; line < rows.Count; line++)
        {
            var row = rows[line];
            string? Cell(string field) => index.TryGetValue(field, out var at) ? LibraryCsv.Value(row, at) : null;
            DateOnly? Day(string field) => ParseDate(Cell(field), spec.DateFormat);
            var (title, series, number) = spec.Columns.ContainsKey("Series") ? (LibraryCsv.Clip(Cell("Title"), 200), LibraryCsv.Clip(Cell("Series"), 200), Number(Cell("SeriesNumber"))) : LibraryCsv.SplitSeries(Cell("Title"));
            var author = LibraryCsv.Clip(Cell("Author"), 200);
            var word = Cell("Status");
            var finished = Day("FinishedOn");
            var status = word is null ? null : statuses.TryGetValue(Plain(word), out var mapped) ? mapped : StatusOf(word);
            status ??= finished is not null ? BookStatus.Finished : BookStatus.Want;
            if (title is null || author is null)
            {
                seen.Add(new RecipeRow(line + 1, title, author, status, title is null ? T("No title") : T("No author")));
                continue;
            }

            var separator = string.IsNullOrEmpty(spec.TagSeparator) ? ";" : spec.TagSeparator;
            var tags = (Cell("Tags") ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(tag => StatusOf(tag) is null).ToList();
            var rating = Cell("Rating") is { } stars && spec.RatingOutOfTen && double.TryParse(stars, NumberStyles.Float, CultureInfo.InvariantCulture, out var ten)
                ? LibraryCsv.Rating((ten / 2).ToString(CultureInfo.InvariantCulture))
                : LibraryCsv.Rating(Cell("Rating"));
            var write = BookWrite.From(
                title, author, status.Value, rating, LibraryCsv.Year(Cell("Year")) ?? Day("Year")?.Year, LibraryCsv.Isbn(Cell("Isbn")), LibraryCsv.Pages(Cell("Pages")),
                null, LibraryCsv.Clip(Cell("Notes"), 4000), Day("StartedOn") ?? (status == BookStatus.Finished ? finished : null), status == BookStatus.Finished ? finished : null, null,
                LibraryCsv.Tags(tags), LibraryCsv.Clip(Cell("Subtitle"), 200), LibraryCsv.Clip(Cell("Publisher"), 200), LibraryCsv.Clip(Cell("Language"), 40),
                BookRules.ParseFormat(Cell("Format")), series, series is null ? null : number, null, LibraryCsv.Clip(LibraryCsv.Review(Cell("Review")), 4000), false, null,
                LibraryCsv.Clip(Cell("Location"), 80), null, LibraryCsv.Clip(Cell("Translator"), 200), null, null, null, null, false, null, null, LibraryCsv.Clip(Cell("Narrator"), 200));
            var added = Day("AddedAt") is DateOnly day ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : (DateTimeOffset?)null;
            books.Add(new CsvBook(write, added));
            seen.Add(new RecipeRow(line + 1, title, author, status, null));
        }

        return (books, seen);
    }

    private static int? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 999 ? (int)number : null;

    private static DateOnly? ParseDate(string? value, string? format)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (format is { Length: > 0 } && DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return exact;
        }

        // A time after the date is left off; a year alone is the year's first day.
        var first = text.Split([' ', 'T'], 2)[0];
        foreach (var candidate in new[] { text, first })
        {
            if (DateOnly.TryParseExact(candidate, [.. DateFormats], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year is >= 1000 and <= 2100 ? new DateOnly(year, 1, 1) : null;
    }

    public static RecipePreview Preview(string text, RecipeSpec? spec)
    {
        var rows = LibraryCsv.Parse(text);
        var header = rows.Count == 0 ? [] : rows[0].Select(name => name.Trim()).Where(name => name.Length > 0).ToArray();
        spec ??= Guess(header);
        var status = spec.Columns.TryGetValue("Status", out var column) ? Array.FindIndex(rows.FirstOrDefault() ?? [], name => name.Trim().Equals(column, StringComparison.OrdinalIgnoreCase)) : -1;
        var words = status < 0 ? [] : rows.Skip(1).Select(row => LibraryCsv.Value(row, status)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
        var (_, read) = Read(rows.Take(PreviewRows + 1).ToList(), spec);
        return new RecipePreview(header, spec, words, [.. read], Math.Max(0, rows.Count - 1));
    }

    // The file brought in with the recipe, under one change so it can be undone; kept as a recipe when named.
    public static async Task<RecipeImportResult> ImportAsync(ShelfDb db, string text, RecipeSpec spec, string? saveAs, int? recipeId, CancellationToken cancellationToken = default)
    {
        var (books, _) = Read(LibraryCsv.Parse(text), spec);
        var result = await BookRules.ImportCsvAsync(db, new CsvLibrary("recipe", books), cancellationToken);
        var batch = await db.ChangeBatches.Where(item => item.OwnerId == db.ReaderId && item.Kind == BatchKind.Import).OrderByDescending(item => item.Id).Select(item => (int?)item.Id).FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(saveAs))
        {
            recipeId = (await SaveAsync(db, saveAs, spec, cancellationToken)).Id;
        }

        if (recipeId is int used)
        {
            var now = DateTimeOffset.UtcNow;
            await db.ImportRecipes.Where(item => item.Id == used).ExecuteUpdateAsync(set => set.SetProperty(item => item.LastUsedAt, now), cancellationToken);
        }

        return new RecipeImportResult(result.Added, result.Skipped, recipeId, batch);
    }

    // Kept by name: a recipe of the same name is replaced.
    public static async Task<ImportRecipe> SaveAsync(ShelfDb db, string name, RecipeSpec spec, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        trimmed = trimmed.Length > ImportRecipe.MaxNameLength ? trimmed[..ImportRecipe.MaxNameLength] : trimmed;
        var recipe = await db.ImportRecipes.FirstOrDefaultAsync(item => item.Name == trimmed, cancellationToken);
        if (recipe is null)
        {
            recipe = new ImportRecipe { OwnerId = db.ReaderId, Name = trimmed, Spec = "", CreatedAt = DateTimeOffset.UtcNow };
            db.ImportRecipes.Add(recipe);
        }

        recipe.Spec = JsonSerializer.Serialize(Clean(spec), History.JsonOptions);
        await db.SaveChangesAsync(cancellationToken);
        return recipe;
    }

    // Only details a recipe knows, and a date format it knows, are kept.
    private static RecipeSpec Clean(RecipeSpec spec) => spec with
    {
        Columns = spec.Columns.Where(pair => Fields.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value.Trim()),
        Statuses = spec.Statuses?.Where(pair => pair.Key.Length is > 0 and <= 80 && Enum.IsDefined(pair.Value)).Take(50).ToDictionary(),
        TagSeparator = spec.TagSeparator is { Length: > 0 and <= 4 } separator ? separator : null,
        DateFormat = spec.DateFormat is { } format && DateFormats.Contains(format) ? format : null,
    };

    public static async Task<RecipeView[]> ListAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        [.. (await db.ImportRecipes.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken)).Select(View)];

    public static RecipeView View(ImportRecipe recipe) =>
        new(recipe.Id, recipe.Name, JsonSerializer.Deserialize<RecipeSpec>(recipe.Spec, History.JsonOptions) ?? new RecipeSpec([]), recipe.CreatedAt, recipe.LastUsedAt);

    public static void Map(RouteGroupBuilder books)
    {
        var recipes = books.MapGroup("/import/recipes").WithTags("Shelf");
        recipes.MapGet("/", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ListAsync(db, cancellationToken)));
        recipes.MapPost("/", async Task<Results<Ok<RecipeView>, ValidationProblem>> (SaveRecipeRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            string.IsNullOrWhiteSpace(request.Name)
                ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [T("Give the recipe a name.")] })
                : TypedResults.Ok(View(await SaveAsync(db, request.Name, request.Spec, cancellationToken))));
        recipes.MapDelete("/{id:int}", async (int id, ShelfDb db, CancellationToken cancellationToken) =>
        {
            await db.ImportRecipes.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken);
            return TypedResults.NoContent();
        });
        recipes.MapPost("/preview", async Task<IResult> (IFormFile? file, [Microsoft.AspNetCore.Mvc.FromForm] string? spec, [Microsoft.AspNetCore.Mvc.FromForm] int? recipeId, ShelfDb db, CancellationToken cancellationToken) =>
            await TextAsync(file, cancellationToken) is not { } text
                ? TypedResults.BadRequest()
                : TypedResults.Ok(Preview(text, await SpecAsync(db, spec, recipeId, cancellationToken)))).DisableAntiforgery();
        recipes.MapPost("/import", async Task<IResult> (IFormFile? file, [Microsoft.AspNetCore.Mvc.FromForm] string? spec, [Microsoft.AspNetCore.Mvc.FromForm] int? recipeId, [Microsoft.AspNetCore.Mvc.FromForm] string? saveAs, ShelfDb db, CancellationToken cancellationToken) =>
        {
            if (await TextAsync(file, cancellationToken) is not { } text)
            {
                return TypedResults.BadRequest();
            }

            var recipe = await SpecAsync(db, spec, recipeId, cancellationToken)
                ?? (string.IsNullOrWhiteSpace(spec) && recipeId is null ? Guess([.. (LibraryCsv.Parse(text).FirstOrDefault() ?? []).Select(name => name.Trim())]) : null);
            if (recipe is null)
            {
                return TypedResults.BadRequest();
            }

            return TypedResults.Ok(await ImportAsync(db, text, recipe, saveAs, recipeId, cancellationToken));
        }).DisableAntiforgery();
    }

    public sealed record SaveRecipeRequest(string Name, RecipeSpec Spec);

    private static async Task<string?> TextAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length is 0 or > MaxBytes)
        {
            return null;
        }

        using var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    // The recipe sent, or the one kept under that id, or none to guess one.
    private static async Task<RecipeSpec?> SpecAsync(ShelfDb db, string? spec, int? recipeId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(spec))
        {
            try
            {
                return JsonSerializer.Deserialize<RecipeSpec>(spec, History.JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return recipeId is int id && await db.ImportRecipes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken) is { } kept ? View(kept).Spec : null;
    }
}
