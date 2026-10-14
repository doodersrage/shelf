using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record BulkRequest(
    int[] Ids,
    BookStatus? Status = null,
    string? AddTag = null,
    string? RemoveTag = null,
    bool? Loved = null,
    bool Delete = false);

public sealed record BulkResult(int Changed);

// One change to many books at once. Only the reader's own books are touched, whatever ids arrive.
public static class Bulk
{
    public const int MaxBooks = 1000;

    public static async Task<BulkResult> ApplyAsync(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CoverStore covers,
        BulkRequest request,
        CancellationToken cancellationToken = default)
    {
        var ids = (request.Ids ?? []).Distinct().Take(MaxBooks).ToList();
        if (ids.Count == 0)
        {
            return new BulkResult(0);
        }

        // Into the trash together, to come back together.
        if (request.Delete)
        {
            var mine = await db.Books.Where(book => ids.Contains(book.Id)).Select(book => book.Id).ToListAsync(cancellationToken);
            if (mine.Count == 0)
            {
                return new BulkResult(0);
            }

            var deleted = await History.StartAsync(db, BatchKind.Delete, mine.Count == 1 ? T("Deleted 1 book") : T("Deleted {0} books", mine.Count), cancellationToken);
            return new BulkResult(await Trash.DeleteBooksAsync(db, ebooks, audio, covers, mine, deleted.Id, cancellationToken));
        }

        var books = await db.Books.Include(book => book.Tags).Where(book => ids.Contains(book.Id)).ToListAsync(cancellationToken);
        if (books.Count == 0)
        {
            return new BulkResult(0);
        }

        var batch = await History.StartAsync(db, BatchKind.Edit, Summary(request, books.Count), cancellationToken);
        using var _ = History.Use(batch.Id);

        var add = BookRules.CanonicalTag(request.AddTag) is { Length: <= BookRules.MaxTagLength } addName ? addName : null;
        var remove = BookRules.CanonicalTag(request.RemoveTag);
        Tag? addTag = null;
        if (add is not null)
        {
            addTag = await db.Tags.FirstOrDefaultAsync(tag => tag.Name == add, cancellationToken) ?? db.Tags.Add(new Tag { Name = add }).Entity;
        }

        foreach (var book in books)
        {
            if (request.Status is { } status)
            {
                BookRules.ChangeStatus(book, status);
            }

            if (request.Loved is { } loved)
            {
                book.Loved = loved;
            }

            if (addTag is not null && book.Tags.Count < BookRules.MaxTags && book.Tags.All(tag => tag.Name != add))
            {
                book.Tags.Add(addTag);
            }

            if (remove is not null)
            {
                book.Tags.RemoveAll(tag => tag.Name == remove);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        return new BulkResult(books.Count);
    }

    // What the change was, for the list of changes that can be undone.
    private static string Summary(BulkRequest request, int count)
    {
        var parts = new List<string>();
        if (request.Status is { } status)
        {
            parts.Add(T("marked {0}", BookRules.StatusLabel(status)));
        }

        if (BookRules.CanonicalTag(request.AddTag) is { } add)
        {
            parts.Add(T("tagged {0}", add));
        }

        if (BookRules.CanonicalTag(request.RemoveTag) is { } remove)
        {
            parts.Add(T("{0} taken off", remove));
        }

        if (request.Loved is { } loved)
        {
            parts.Add(loved ? T("loved") : T("no longer loved"));
        }

        var what = parts.Count == 0 ? T("changed") : string.Join(", ", parts);
        return count == 1 ? T("1 book {0}", what) : T("{0} books {1}", count, what);
    }

    public static async Task<Results<Ok<BulkResult>, ValidationProblem>> Apply(
        BulkRequest request,
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CoverStore covers,
        CancellationToken cancellationToken)
    {
        if (request.Ids is null || request.Ids.Length == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Ids)] = [Localization.Words.T("Choose at least one book.")] });
        }

        if (request.Ids.Length > MaxBooks)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Ids)] = [Localization.Words.T("Change at most {0} books at once.", MaxBooks)] });
        }

        return TypedResults.Ok(await ApplyAsync(db, ebooks, audio, covers, request, cancellationToken));
    }
}
