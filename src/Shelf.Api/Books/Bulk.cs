using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

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

        var books = await db.Books.Include(book => book.Tags).Where(book => ids.Contains(book.Id)).ToListAsync(cancellationToken);
        if (request.Delete)
        {
            foreach (var book in books)
            {
                ebooks.Delete(book.EbookStoredName);
                audio.Delete(book.AudioStoredName);
                covers.Delete(book.CoverImage);
            }

            db.Books.RemoveRange(books);
            await db.SaveChangesAsync(cancellationToken);
            await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
            return new BulkResult(books.Count);
        }

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
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Ids)] = ["Choose at least one book."] });
        }

        if (request.Ids.Length > MaxBooks)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Ids)] = [$"Change at most {MaxBooks} books at once."] });
        }

        return TypedResults.Ok(await ApplyAsync(db, ebooks, audio, covers, request, cancellationToken));
    }
}
