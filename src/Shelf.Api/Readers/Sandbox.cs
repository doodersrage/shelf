using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Readers;

public sealed record SandboxInfo(int Books, ApiTokenSummary[] Tokens);

public sealed record SandboxTokenRequest(string? Name, bool CanChange = true, string[]? Areas = null);

// A shelf of sample books to try a connector, a script, or a webhook on before it is given the real one. It is a
// reader of its own that no one signs in as and no one else sees: what a token for it reads and writes is only ever
// the samples, and it can lend to, ask of, or join no one. Resetting brings the samples back as they were.
public sealed class Sandbox(IServiceScopeFactory scopes, EbookStore ebooks, AudioStore audio, CoverStore covers)
{
    public static Task<int?> IdOfAsync(ShelfDb db, int ownerId, CancellationToken cancellationToken = default) =>
        db.Readers.AsNoTracking().Where(reader => reader.SandboxOf == ownerId).Select(reader => (int?)reader.Id).FirstOrDefaultAsync(cancellationToken);

    public static Task<bool> IsSandboxAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default) =>
        db.Readers.AsNoTracking().AnyAsync(reader => reader.Id == readerId && reader.SandboxOf != null, cancellationToken);

    public async Task<SandboxInfo?> InfoAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        if (await IdOfAsync(db, db.ReaderId, cancellationToken) is not int id)
        {
            return null;
        }

        var books = await db.Books.IgnoreQueryFilters().CountAsync(book => book.OwnerId == id, cancellationToken);
        var tokens = await db.ApiTokens.AsNoTracking().Where(token => token.ReaderId == id).OrderByDescending(token => token.Id)
            .Select(token => new ApiTokenSummary(token.Id, token.Name, token.CanChange, token.CreatedAt, token.LastUsedAt, token.Areas == null ? null : token.Areas.Split(',', StringSplitOptions.None)))
            .ToListAsync(cancellationToken);
        return new SandboxInfo(books, [.. tokens]);
    }

    // A token for the reader's sandbox, made with its samples the first time.
    public async Task<(NewApiToken? Made, string? Problem)> TokenAsync(ShelfDb db, SandboxTokenRequest request, CancellationToken cancellationToken = default)
    {
        if (await Family.IsRestrictedAsync(db, cancellationToken: cancellationToken))
        {
            return (null, T("This account can't make a sandbox."));
        }

        var id = await IdOfAsync(db, db.ReaderId, cancellationToken) ?? await MakeAsync(db, cancellationToken);
        return await ApiTokens.CreateAsync(db, id, string.IsNullOrWhiteSpace(request.Name) ? T("Sandbox") : request.Name, request.CanChange, cancellationToken, request.Areas);
    }

    // The samples brought back as they were; its tokens go on working.
    public async Task ResetAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        if (await IdOfAsync(db, db.ReaderId, cancellationToken) is not int old)
        {
            return;
        }

        var fresh = await MakeAsync(db, cancellationToken);
        await db.ApiTokens.Where(token => token.ReaderId == old).ExecuteUpdateAsync(set => set.SetProperty(token => token.ReaderId, fresh), cancellationToken);
        await db.Webhooks.IgnoreQueryFilters().Where(hook => hook.ReaderId == old).ExecuteUpdateAsync(set => set.SetProperty(hook => hook.ReaderId, fresh), cancellationToken);
        await ReaderRules.RemoveAsync(db, ebooks, audio, covers, old, cancellationToken);
    }

    public async Task RemoveAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        if (await IdOfAsync(db, db.ReaderId, cancellationToken) is int id)
        {
            await ReaderRules.RemoveAsync(db, ebooks, audio, covers, id, cancellationToken);
        }
    }

    private async Task<int> MakeAsync(ShelfDb db, CancellationToken cancellationToken)
    {
        var owner = await db.Readers.AsNoTracking().FirstAsync(reader => reader.Id == db.ReaderId, cancellationToken);

        // A name no one can sign up with, and a password no one knows.
        var reader = new Reader
        {
            Name = T("Sandbox of {0}", owner.Name),
            NormalizedName = "SANDBOX " + Guid.NewGuid().ToString("N").ToUpperInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
            SandboxOf = owner.Id,
            PasswordUnknown = true,
            Language = owner.Language,
        };
        reader.PasswordHash = ReaderRules.Hasher.HashPassword(reader, ReaderRules.NewPassword() + Guid.NewGuid());
        db.Readers.Add(reader);
        await db.SaveChangesAsync(cancellationToken);
        await SeedAsync(reader.Id, cancellationToken);
        return reader.Id;
    }

    private sealed record Sample(string Title, string Author, int Year, BookStatus Status, int? Rating, string[] Tags, int Pages, int? AtPage = null, string? Quote = null, string? LentTo = null, bool Loved = false);

    private static readonly Sample[] Samples =
    [
        new("Pride and Prejudice", "Jane Austen", 1813, BookStatus.Finished, 5, ["classics", "romance"], 432, Quote: "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife.", Loved: true),
        new("Moby-Dick", "Herman Melville", 1851, BookStatus.Reading, null, ["classics", "sea"], 635, AtPage: 212, Quote: "Call me Ishmael."),
        new("Frankenstein", "Mary Shelley", 1818, BookStatus.Finished, 4, ["gothic", "science fiction"], 280),
        new("The Adventures of Sherlock Holmes", "Arthur Conan Doyle", 1892, BookStatus.Want, null, ["mystery"], 307),
        new("Middlemarch", "George Eliot", 1871, BookStatus.Want, null, ["classics"], 880),
        new("Dracula", "Bram Stoker", 1897, BookStatus.Abandoned, 2, ["gothic"], 418),
        new("The Time Machine", "H. G. Wells", 1895, BookStatus.Finished, 3, ["science fiction"], 118),
        new("Little Women", "Louisa May Alcott", 1868, BookStatus.Want, null, ["classics"], 449),
        new("Walden", "Henry David Thoreau", 1854, BookStatus.Finished, 4, ["nature", "essays"], 352, Quote: "I went to the woods because I wished to live deliberately."),
        new("The Odyssey", "Homer", -700, BookStatus.Want, null, ["classics", "myth"], 541, LentTo: "Sam"),
        new("Jane Eyre", "Charlotte Brontë", 1847, BookStatus.Reading, null, ["classics", "gothic"], 532, AtPage: 120),
        new("Treasure Island", "Robert Louis Stevenson", 1883, BookStatus.Want, null, ["sea", "adventure"], 292),
    ];

    // The samples, made as the sandbox's own, the way the shelf would make them.
    private async Task SeedAsync(int sandboxId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(sandboxId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var made = new List<Book>();
        for (var at = 0; at < Samples.Length; at++)
        {
            var sample = Samples[at];
            var book = new Book
            {
                OwnerId = sandboxId, Title = sample.Title, Author = sample.Author, Status = sample.Status, Rating = sample.Rating, Pages = sample.Pages,
                Year = sample.Year is >= 1000 and <= 2100 ? sample.Year : null, Loved = sample.Loved, Language = "English",
                AddedAt = DateTimeOffset.UtcNow.AddDays(-30 * (Samples.Length - at)),
                CurrentPage = sample.AtPage,
                StartedOn = sample.Status is BookStatus.Reading or BookStatus.Finished or BookStatus.Abandoned ? today.AddDays(-20 - at * 9) : null,
                FinishedOn = sample.Status == BookStatus.Finished ? today.AddDays(-5 - at * 9) : null,
                LoanedTo = sample.LentTo, LoanedOn = sample.LentTo is null ? null : today.AddDays(-4), DueOn = sample.LentTo is null ? null : today.AddDays(10),
                Location = at % 3 == 0 ? "Study" : "Living room",
            };
            if (sample.Quote is { } quote)
            {
                book.Quotes.Add(new Quote { Text = quote, Page = 1, NotedAt = DateTimeOffset.UtcNow.AddDays(-at) });
            }

            if (book.StartedOn is DateOnly started)
            {
                book.Sessions.Add(new ReadingSession { Date = started.AddDays(2), FromPage = 1, ToPage = Math.Min(sample.Pages, 40) });
            }

            db.Books.Add(book);
            await BookRules.SyncTagsAsync(db, book, sample.Tags, cancellationToken);
            made.Add(book);
        }

        await db.SaveChangesAsync(cancellationToken);
        db.Collections.Add(new Collection
        {
            OwnerId = sandboxId, Name = "Gothic nights", Description = "Old houses, storms, and things in the dark.", CreatedAt = DateTimeOffset.UtcNow,
            Books = [.. made.Where(book => book.Tags.Any(tag => tag.Name == "gothic")).Select((book, position) => new CollectionBook { BookId = book.Id, Position = position, AddedAt = DateTimeOffset.UtcNow })],
        });
        db.JournalEntries.Add(new JournalEntry { OwnerId = sandboxId, Date = today.AddDays(-3), Text = "Read Moby-Dick on the ferry; the chapter on whiteness stays with me.", BookId = made[1].Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        var sandbox = app.MapGroup("/account/sandbox").WithTags("Account").RequireAuthorization();
        sandbox.MapGet("/", async Task<Results<Ok<SandboxInfo>, NoContent>> (ShelfDb db, Sandbox service, CancellationToken cancellationToken) =>
            await service.InfoAsync(db, cancellationToken) is { } info ? TypedResults.Ok(info) : TypedResults.NoContent());
        sandbox.MapPost("/tokens", async Task<Results<Ok<NewApiToken>, ValidationProblem>> (SandboxTokenRequest request, ShelfDb db, Sandbox service, CancellationToken cancellationToken) =>
            await service.TokenAsync(db, request, cancellationToken) is ({ } made, _)
                ? TypedResults.Ok(made)
                : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [T("This account can't make a sandbox.")] }));
        sandbox.MapPost("/reset", async (ShelfDb db, Sandbox service, CancellationToken cancellationToken) =>
        {
            await service.ResetAsync(db, cancellationToken);
            return TypedResults.NoContent();
        });
        sandbox.MapDelete("/", async (ShelfDb db, Sandbox service, CancellationToken cancellationToken) =>
        {
            await service.RemoveAsync(db, cancellationToken);
            return TypedResults.NoContent();
        });
    }
}
