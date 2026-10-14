using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// Open Library's and Google's answers for one book, asked apart so a review can show where they differ.
public interface ICatalogSources
{
    Task<(CatalogMatch? OpenLibrary, CatalogMatch? Google)> BothAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken);
}

public sealed class CatalogSources(OpenLibraryLookup openLibrary, GoogleBooksLookup google) : ICatalogSources
{
    public async Task<(CatalogMatch? OpenLibrary, CatalogMatch? Google)> BothAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken)
    {
        var first = await openLibrary.FindAsync(isbn, title, author, cancellationToken);
        var second = await google.FindAsync(isbn ?? first?.Isbn, title, author, cancellationToken);
        return (first, second);
    }
}

// One detail of one book the catalogs would change, kept for the reader to look over before anything changes:
// what it is now, and what each catalog says.
public sealed class DetailProposal
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public required string Field { get; set; }
    public string? Current { get; set; }
    public string? OpenLibrary { get; set; }
    public string? Google { get; set; }
    public DateTimeOffset FoundAt { get; set; }
}

public enum DetailSource
{
    OpenLibrary,
    Google,
    Keep,
}

public sealed record ProposalView(int Id, string Field, string Label, string? Current, string? OpenLibrary, string? Google, bool Disagree);

public sealed record ProposedBook(int BookId, string Title, string Author, ProposalView[] Changes);

public sealed record ProposalChoice(int Id, DetailSource Source);

public sealed record ApplyProposalsRequest(ProposalChoice[]? Choices, bool AllAgreeing = false);

public sealed record AppliedProposals(int Changed, int? BatchId);

public static class DetailReview
{
    // The details a catalog can speak to. The title and author are the reader's own and never proposed.
    public static readonly IReadOnlyList<string> Fields =
        [nameof(Book.Subtitle), nameof(Book.Year), nameof(Book.Pages), nameof(Book.Publisher), nameof(Book.Language), nameof(Book.Isbn), nameof(Book.Series), nameof(Book.SeriesNumber), nameof(Book.CoverUrl)];

    // The catalogs' answers turned into proposals, replacing any kept for the book before. Says how many.
    public static async Task<int> ProposeAsync(ShelfDb db, Book book, CatalogMatch? openLibrary, CatalogMatch? google, CancellationToken cancellationToken = default)
    {
        await db.DetailProposals.Where(item => item.BookId == book.Id).ExecuteDeleteAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var count = 0;
        foreach (var field in Fields)
        {
            var current = Value(book, field);
            var (first, second) = (Offered(openLibrary, field), Offered(google, field));
            if ((first is null || Same(field, first, current)) && (second is null || Same(field, second, current)))
            {
                continue;
            }

            db.DetailProposals.Add(new DetailProposal
            {
                OwnerId = book.OwnerId ?? db.ReaderId, BookId = book.Id, Field = field, FoundAt = now,
                Current = Cut(current), OpenLibrary = Cut(first), Google = Cut(second),
            });
            count++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return count;
    }

    private static string? Value(Book book, string field) => field switch
    {
        nameof(Book.Subtitle) => Blank(book.Subtitle),
        nameof(Book.Year) => book.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        nameof(Book.Pages) => book.Pages?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        nameof(Book.Publisher) => Blank(book.Publisher),
        nameof(Book.Language) => Blank(book.Language),
        nameof(Book.Isbn) => Blank(book.Isbn),
        nameof(Book.Series) => Blank(book.Series),
        nameof(Book.SeriesNumber) => book.SeriesNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Blank(book.CoverUrl),
    };

    // What a catalog offers for a field, only when it would be a fit value for the book.
    private static string? Offered(CatalogMatch? match, string field) => match is null ? null : field switch
    {
        nameof(Book.Subtitle) => Blank(match.Subtitle),
        nameof(Book.Year) => match.Year is >= 1000 and <= 2100 ? match.Year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
        nameof(Book.Pages) => match.Pages is >= 1 and <= 20000 ? match.Pages.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
        nameof(Book.Publisher) => Blank(match.Publisher),
        nameof(Book.Language) => Blank(match.Language) is { Length: <= 40 } language ? language : null,
        nameof(Book.Isbn) => BookRules.NormalizeIsbn(match.Isbn),
        nameof(Book.Series) => Blank(match.Series),
        nameof(Book.SeriesNumber) => match.SeriesNumber is >= 0 and <= 10000 ? match.SeriesNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
        _ => Blank(match.CoverUrl) is { Length: <= 500 } cover && BookRules.IsCoverUrl(cover) ? cover : null,
    };

    // Told apart only by case or spacing, or by the dashes in an ISBN, is the same.
    private static bool Same(string field, string? one, string? other) =>
        field == nameof(Book.Isbn)
            ? BookRules.NormalizeIsbn(one) == BookRules.NormalizeIsbn(other)
            : string.Equals(Squash(one), Squash(other), StringComparison.CurrentCultureIgnoreCase);

    private static string? Squash(string? text) => text is null ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Cut(string? text) => text is { Length: > 500 } ? text[..500] : text;

    public static async Task<ProposedBook[]> ListAsync(ShelfDb db, int take = 500, CancellationToken cancellationToken = default)
    {
        var rows = await db.DetailProposals.AsNoTracking()
            .OrderBy(item => item.Book!.Title).ThenBy(item => item.BookId).ThenBy(item => item.Id)
            .Select(item => new { Proposal = item, item.Book!.Title, item.Book.Author })
            .Take(take * Fields.Count)
            .ToListAsync(cancellationToken);
        return [.. rows.GroupBy(row => row.Proposal.BookId).Take(take).Select(group => new ProposedBook(group.Key, group.First().Title, group.First().Author,
            [.. group.Select(row => View(row.Proposal))]))];
    }

    public static Task<int> CountAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        db.DetailProposals.Select(item => item.BookId).Distinct().CountAsync(cancellationToken);

    private static ProposalView View(DetailProposal item) =>
        new(item.Id, item.Field, History.Label(item.Field), item.Current, item.OpenLibrary, item.Google, Disagree(item));

    // Both catalogs speak, and say different things.
    private static bool Disagree(DetailProposal item) =>
        item.OpenLibrary is not null && item.Google is not null && !Same(item.Field, item.OpenLibrary, item.Google);

    // The chosen answers written to the books, together, so they can be undone at once. With allAgreeing, every
    // proposal the catalogs do not disagree on is taken too. A proposal chosen or kept is done with.
    public static async Task<AppliedProposals> ApplyAsync(ShelfDb db, IReadOnlyCollection<ProposalChoice> choices, bool allAgreeing, CancellationToken cancellationToken = default)
    {
        var chosen = choices.Where(choice => Enum.IsDefined(choice.Source)).GroupBy(choice => choice.Id).ToDictionary(group => group.Key, group => group.Last().Source);
        var ids = chosen.Keys.ToList();
        var proposals = await db.DetailProposals.AsNoTracking()
            .Where(item => ids.Contains(item.Id) || allAgreeing)
            .ToListAsync(cancellationToken);
        var take = proposals
            .Select(item => (item, source: chosen.TryGetValue(item.Id, out var source) ? source : Disagree(item) ? (DetailSource?)null : item.OpenLibrary is not null ? DetailSource.OpenLibrary : DetailSource.Google))
            .Where(pair => pair.source is not null)
            .ToList();
        if (take.Count == 0)
        {
            return new AppliedProposals(0, null);
        }

        var changes = take.Where(pair => pair.source != DetailSource.Keep).ToList();
        ChangeBatch? batch = null;
        var changed = 0;
        if (changes.Count > 0)
        {
            var bookCount = changes.Select(pair => pair.item.BookId).Distinct().Count();
            batch = await History.StartAsync(db, BatchKind.Details, bookCount == 1 ? T("Details of 1 book updated") : T("Details of {0} books updated", bookCount), cancellationToken);
            using var _ = History.Use(batch.Id);
            foreach (var chunk in changes.GroupBy(pair => pair.item.BookId).Chunk(200))
            {
                var bookIds = chunk.Select(group => group.Key).ToList();
                var books = await db.Books.Where(book => bookIds.Contains(book.Id)).ToDictionaryAsync(book => book.Id, cancellationToken);
                foreach (var group in chunk)
                {
                    if (!books.TryGetValue(group.Key, out var book))
                    {
                        continue;
                    }

                    foreach (var (item, source) in group)
                    {
                        var value = source == DetailSource.OpenLibrary ? item.OpenLibrary : item.Google;
                        if (value is not null && Set(book, item.Field, value))
                        {
                            changed++;
                        }
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
            }
        }

        var done = take.Select(pair => pair.item.Id).ToList();
        foreach (var chunk in done.Chunk(500))
        {
            await db.DetailProposals.Where(item => chunk.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        return new AppliedProposals(changed, batch?.Id);
    }

    private static bool Set(Book book, string field, string value)
    {
        int? Number() => int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
        string Text(int max) => value.Length <= max ? value : value[..max];
        switch (field)
        {
            case nameof(Book.Subtitle): book.Subtitle = Text(200); break;
            case nameof(Book.Year): book.Year = Number() ?? book.Year; break;
            case nameof(Book.Pages): book.Pages = Number() ?? book.Pages; break;
            case nameof(Book.Publisher): book.Publisher = Text(200); break;
            case nameof(Book.Language): book.Language = Text(40); break;
            case nameof(Book.Isbn): book.Isbn = BookRules.NormalizeIsbn(value) ?? book.Isbn; break;
            case nameof(Book.Series): book.Series = Text(200); break;
            case nameof(Book.SeriesNumber): book.SeriesNumber = Number() ?? book.SeriesNumber; break;
            case nameof(Book.CoverUrl) when BookRules.IsCoverUrl(value): book.CoverUrl = value; break;
            default: return false;
        }

        return true;
    }

    public static void Map(RouteGroupBuilder books)
    {
        var details = books.MapGroup("/details").WithTags("Details");
        details.MapPost("/review", async Task<IResult> (ReviewRequest? request, ShelfDb db, DetailsFiller filler, CancellationToken cancellationToken) =>
            await filler.StartAsync(db, request?.Everything == true ? DetailsKind.ReviewAll : DetailsKind.Review, cancellationToken) is { } job
                ? TypedResults.Accepted("/books/details/proposals", new { job.Id, Books = job.Books.Count })
                : TypedResults.NoContent());
        details.MapGet("/proposals", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ListAsync(db, cancellationToken: cancellationToken)));
        details.MapPost("/proposals/apply", async (ApplyProposalsRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            TypedResults.Ok(await ApplyAsync(db, request.Choices ?? [], request.AllAgreeing, cancellationToken)));
        details.MapDelete("/proposals", async (ShelfDb db, CancellationToken cancellationToken) =>
        {
            await db.DetailProposals.ExecuteDeleteAsync(cancellationToken);
            return TypedResults.NoContent();
        });
    }

    public sealed record ReviewRequest(bool Everything = false);
}
