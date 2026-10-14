using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// What a reader wants done with their library should they be unable to see to it: a word for whoever will, and
// who is to have which books. It is kept sealed with the server's keys and shown to no one but the reader; it is a
// note of wishes, not a will.
public sealed class LegacyPlan
{
    public const int MaxInstructionsLength = 8000;

    public int Id { get; set; }
    public int OwnerId { get; set; }

    // Sealed: the words for whoever sees to the library, and who that is.
    public string? Instructions { get; set; }
    public string? Keeper { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LegacyRecipient
{
    public const int MaxPerReader = 30;
    public const int MaxNoteLength = 2000;

    public int Id { get; set; }
    public int OwnerId { get; set; }

    // Sealed: who they are, how to reach them, and a word for them.
    public required string Name { get; set; }
    public string? Contact { get; set; }
    public string? Note { get; set; }

    // Which books: those with a tag, those in a collection, or whatever no one else is to have.
    public string? Tag { get; set; }
    public int? CollectionId { get; set; }
    public bool Rest { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record LegacyPlanRequest(string? Instructions, string? Keeper);

public sealed record LegacyView(string? Instructions, string? Keeper, DateTimeOffset? UpdatedAt, LegacyRecipientView[] Recipients, int Books, int Unassigned);

public sealed record LegacyRecipientView(int Id, string Name, string? Contact, string? Note, string? Tag, int? CollectionId, string? Collection, bool Rest, int Books);

public sealed record LegacyRecipientRequest(string Name, string? Contact, string? Note, string? Tag, int? CollectionId, bool Rest);

public sealed record LegacyBook(int Id, string Title, string Author, int? Year, string? Isbn, string? Location, BookFormat? Format, decimal? PricePaid, bool Signed, string? Inscription);

public sealed record LegacyShare(string Recipient, LegacyBook[] Books);

public sealed class Legacy(IDataProtectionProvider protection)
{
    private IDataProtector Protector => protection.CreateProtector("Shelf.Legacy");

    public static void Map(RouteGroupBuilder books)
    {
        var legacy = books.MapGroup("/legacy").WithTags("Your account");
        legacy.MapGet("/", async (ShelfDb db, Legacy plans, CancellationToken cancellationToken) => TypedResults.Ok(await plans.ViewAsync(db, cancellationToken)));
        legacy.MapPut("/", async Task<IResult> (LegacyPlanRequest request, ShelfDb db, Legacy plans, CancellationToken cancellationToken) =>
            await plans.SavePlanAsync(db, request.Instructions, request.Keeper, cancellationToken) is { } problem
                ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Instructions"] = [problem] })
                : TypedResults.NoContent());
        legacy.MapDelete("/", async (ShelfDb db, CancellationToken cancellationToken) =>
        {
            await ForgetAsync(db, cancellationToken);
            return TypedResults.NoContent();
        });
        legacy.MapPost("/recipients", async Task<IResult> (LegacyRecipientRequest request, ShelfDb db, Legacy plans, CancellationToken cancellationToken) =>
            await plans.SaveRecipientAsync(db, null, request, cancellationToken) is { } problem
                ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [problem] })
                : TypedResults.NoContent());
        legacy.MapPut("/recipients/{id:int}", async Task<IResult> (int id, LegacyRecipientRequest request, ShelfDb db, Legacy plans, CancellationToken cancellationToken) =>
            await plans.SaveRecipientAsync(db, id, request, cancellationToken) is { } problem
                ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [problem] })
                : TypedResults.NoContent());
        legacy.MapDelete("/recipients/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await RemoveRecipientAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        legacy.MapGet("/inventory.csv", async (ShelfDb db, Legacy plans, CancellationToken cancellationToken) =>
            Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(await plans.InventoryCsvAsync(db, cancellationToken))).ToArray(), "text/csv; charset=utf-8", "library-inventory.csv"));
    }

    private string? Seal(string? text) => string.IsNullOrWhiteSpace(text) ? null : Protector.Protect(text.Trim());

    private string? Open(string? sealedText)
    {
        try
        {
            return sealedText is null ? null : Protector.Unprotect(sealedText);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public async Task<LegacyView> ViewAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var plan = await db.LegacyPlans.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var recipients = await db.LegacyRecipients.AsNoTracking().OrderBy(item => item.Rest).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        var shares = await SharesAsync(db, recipients, cancellationToken);
        var collections = await db.Collections.AsNoTracking().Select(item => new { item.Id, item.Name }).ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);
        var total = await db.Books.CountAsync(book => book.LeftOn == null, cancellationToken);
        var assigned = shares.Values.SelectMany(books => books).Select(book => book.Id).Distinct().Count();
        return new LegacyView(Open(plan?.Instructions), Open(plan?.Keeper), plan?.UpdatedAt,
            [.. recipients.Select(item => new LegacyRecipientView(item.Id, Open(item.Name) ?? "", Open(item.Contact), Open(item.Note), item.Tag, item.CollectionId,
                item.CollectionId is int list ? collections.GetValueOrDefault(list) : null, item.Rest, shares.GetValueOrDefault(item.Id)?.Count ?? 0))],
            total, total - assigned);
    }

    public async Task<string?> SavePlanAsync(ShelfDb db, string? instructions, string? keeper, CancellationToken cancellationToken = default)
    {
        if (instructions?.Length > LegacyPlan.MaxInstructionsLength)
        {
            return T("Keep the instructions to {0} characters.", LegacyPlan.MaxInstructionsLength);
        }

        if (keeper?.Length > 200)
        {
            return T("Keep the name to {0} characters.", 200);
        }

        var plan = await db.LegacyPlans.FirstOrDefaultAsync(cancellationToken) ?? db.LegacyPlans.Add(new LegacyPlan { OwnerId = db.ReaderId }).Entity;
        (plan.Instructions, plan.Keeper, plan.UpdatedAt) = (Seal(instructions), Seal(keeper), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task<string?> SaveRecipientAsync(ShelfDb db, int? id, LegacyRecipientRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            return T("Give their name, up to {0} characters.", 200);
        }

        if (request.Note?.Length > LegacyRecipient.MaxNoteLength || request.Contact?.Length > 300)
        {
            return T("Keep the note to {0} characters.", LegacyRecipient.MaxNoteLength);
        }

        var tag = BookRules.CanonicalTag(request.Tag);
        if (!request.Rest && tag is null && request.CollectionId is null)
        {
            return T("Choose their books: a tag, a collection, or everything no one else is to have.");
        }

        if (request.CollectionId is int list && !await db.Collections.AnyAsync(item => item.Id == list, cancellationToken))
        {
            return T("That collection isn't yours.");
        }

        LegacyRecipient? recipient;
        if (id is int existing)
        {
            recipient = await db.LegacyRecipients.FirstOrDefaultAsync(item => item.Id == existing, cancellationToken);
            if (recipient is null)
            {
                return T("That person isn't in your plan.");
            }
        }
        else
        {
            if (await db.LegacyRecipients.CountAsync(cancellationToken) >= LegacyRecipient.MaxPerReader)
            {
                return T("A plan can name up to {0} people.", LegacyRecipient.MaxPerReader);
            }

            recipient = db.LegacyRecipients.Add(new LegacyRecipient { OwnerId = db.ReaderId, Name = "", CreatedAt = DateTimeOffset.UtcNow }).Entity;
        }

        recipient.Name = Seal(name)!;
        (recipient.Contact, recipient.Note) = (Seal(request.Contact), Seal(request.Note));
        (recipient.Tag, recipient.CollectionId, recipient.Rest) = request.Rest ? (null, null, true) : (tag, tag is null ? request.CollectionId : null, false);
        if (recipient.Rest)
        {
            await db.LegacyRecipients.Where(item => item.Rest && item.Id != recipient.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.Rest, false), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // Who is to have which books, as the shelf stands now. A book two people are named for goes to the first.
    public async Task<LegacyShare[]> InventoryAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var recipients = await db.LegacyRecipients.AsNoTracking().OrderBy(item => item.Rest).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        var shares = await SharesAsync(db, recipients, cancellationToken);
        var result = recipients.Select(item => new LegacyShare(Open(item.Name) ?? "", [.. shares.GetValueOrDefault(item.Id) ?? []])).ToList();
        var given = shares.Values.SelectMany(books => books).Select(book => book.Id).ToHashSet();
        var left = (await AllAsync(db, cancellationToken)).Where(book => !given.Contains(book.Id)).ToArray();
        if (left.Length > 0)
        {
            result.Add(new LegacyShare(T("Not yet given to anyone"), left));
        }

        return [.. result];
    }

    public async Task<string> InventoryCsvAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder("Recipient,Title,Author,Year,ISBN,Format,Place,Signed,Inscription,Price paid\n");
        foreach (var share in await InventoryAsync(db, cancellationToken))
        {
            foreach (var book in share.Books)
            {
                text.AppendJoin(',', new[] { share.Recipient, book.Title, book.Author, book.Year?.ToString(), book.Isbn, book.Format?.ToString(), book.Location, book.Signed ? "yes" : "", book.Inscription, book.PricePaid?.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Select(Cell));
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    public static async Task<bool> RemoveRecipientAsync(ShelfDb db, int id, CancellationToken cancellationToken = default) =>
        await db.LegacyRecipients.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    // Forgetting the plan forgets everything in it.
    public static async Task ForgetAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        await db.LegacyRecipients.ExecuteDeleteAsync(cancellationToken);
        await db.LegacyPlans.ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<Dictionary<int, List<LegacyBook>>> SharesAsync(ShelfDb db, List<LegacyRecipient> recipients, CancellationToken cancellationToken)
    {
        var shares = new Dictionary<int, List<LegacyBook>>();
        if (recipients.Count == 0)
        {
            return shares;
        }

        var books = await AllAsync(db, cancellationToken);
        var tagged = await db.Books.AsNoTracking().Where(book => book.LeftOn == null && book.Tags.Any())
            .Select(book => new { book.Id, Tags = book.Tags.Select(tag => tag.Name).ToList() }).ToListAsync(cancellationToken);
        var byTag = tagged.SelectMany(book => book.Tags.Select(name => (book.Id, Name: name))).GroupBy(item => item.Name)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Id).ToHashSet());
        var listIds = recipients.Where(item => item.CollectionId is not null).Select(item => item.CollectionId!.Value).ToList();
        var lists = (await db.CollectionBooks.AsNoTracking().Where(item => listIds.Contains(item.CollectionId)).Select(item => new { item.CollectionId, item.BookId }).ToListAsync(cancellationToken))
            .GroupBy(item => item.CollectionId).ToDictionary(group => group.Key, group => group.Select(item => item.BookId).ToHashSet());
        var given = new HashSet<int>();
        foreach (var recipient in recipients.Where(item => !item.Rest))
        {
            var ids = recipient.Tag is { } tag ? byTag.GetValueOrDefault(tag) ?? [] : lists.GetValueOrDefault(recipient.CollectionId ?? 0) ?? [];
            shares[recipient.Id] = [.. books.Where(book => ids.Contains(book.Id) && given.Add(book.Id))];
        }

        if (recipients.FirstOrDefault(item => item.Rest) is { } rest)
        {
            shares[rest.Id] = [.. books.Where(book => given.Add(book.Id))];
        }

        return shares;
    }

    private static async Task<List<LegacyBook>> AllAsync(ShelfDb db, CancellationToken cancellationToken) =>
        await db.Books.AsNoTracking().Where(book => book.LeftOn == null && book.VisitingLinkId == null)
            .OrderBy(book => book.Author).ThenBy(book => book.Title)
            .Select(book => new LegacyBook(book.Id, book.Title, book.Author, book.Year, book.Isbn, book.Location, book.Format, book.PricePaid, book.Signed, book.Inscription))
            .ToListAsync(cancellationToken);

    private static string Cell(string? value) =>
        value is null ? "" : value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
