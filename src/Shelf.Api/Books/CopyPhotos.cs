using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// A picture of the reader's own copy: an inscription, a signature, a bookplate, the spine. Kept with the covers,
// so the snapshots that keep covers keep these too, and seen only by the copy's owner.
public sealed class CopyPhoto
{
    public const int MaxCaptionLength = 200;
    public const int MaxPerBook = 20;

    public int Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public required string StoredName { get; set; }
    public string? Caption { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed record CopyPhotoResponse(int Id, string? Caption, DateTimeOffset AddedAt);

public static class CopyPhotos
{
    public static void Map(RouteGroupBuilder books)
    {
        // A book not on this reader's shelf is not there, as on every other route of a book, rather than one with no pictures.
        books.MapGet("/{id:int}/photos", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await db.Books.AnyAsync(book => book.Id == id, cancellationToken) ? TypedResults.Ok(await ListAsync(db, id, cancellationToken)) : TypedResults.NotFound());
        books.MapPost("/{id:int}/photos", Upload).DisableAntiforgery();
        books.MapGet("/{id:int}/photos/{photoId:int}", Serve);
        books.MapDelete("/{id:int}/photos/{photoId:int}", async Task<IResult> (int id, int photoId, ShelfDb db, CoverStore covers, CancellationToken cancellationToken) =>
            await RemoveAsync(db, covers, id, photoId, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
    }

    public static async Task<CopyPhotoResponse[]> ListAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default) =>
        (await db.CopyPhotos.AsNoTracking().Where(photo => photo.BookId == bookId).ToListAsync(cancellationToken))
            .OrderBy(photo => photo.AddedAt).ThenBy(photo => photo.Id)
            .Select(photo => new CopyPhotoResponse(photo.Id, photo.Caption, photo.AddedAt))
            .ToArray();

    // A picture from the book's page, which comes back to it saying how it went.
    public static async Task<IResult> Upload(int id, IFormFile? file, [Microsoft.AspNetCore.Mvc.FromForm] string? caption, ShelfDb db, CoverStore covers, CancellationToken cancellationToken)
    {
        if (!await db.Books.AnyAsync(book => book.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        if (await db.CopyPhotos.CountAsync(photo => photo.BookId == id, cancellationToken) >= CopyPhoto.MaxPerBook)
        {
            return TypedResults.Redirect($"/library/{id}?photo=full#copy");
        }

        string? saved = null;
        if (file is { Length: > 0 and <= CoverStore.MaxBytes })
        {
            await using var stream = file.OpenReadStream();
            saved = await covers.SaveAsync(stream, cancellationToken);
        }

        if (saved is null)
        {
            return TypedResults.Redirect($"/library/{id}?photo=unsupported#copy");
        }

        caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        db.CopyPhotos.Add(new CopyPhoto { BookId = id, StoredName = saved, Caption = caption?[..Math.Min(caption.Length, CopyPhoto.MaxCaptionLength)], AddedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Redirect($"/library/{id}?photo=saved#copy");
    }

    public static async Task<IResult> Serve(int id, int photoId, ShelfDb db, CoverStore covers, HttpContext http, CancellationToken cancellationToken)
    {
        var stored = await db.CopyPhotos.AsNoTracking().Where(photo => photo.Id == photoId && photo.BookId == id).Select(photo => photo.StoredName).FirstOrDefaultAsync(cancellationToken);
        if (covers.OpenPath(stored) is not { } path)
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.CacheControl = "private, max-age=86400";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        return Results.File(path, CoverStore.ContentType(stored!));
    }

    public static async Task<bool> RemoveAsync(ShelfDb db, CoverStore covers, int bookId, int photoId, CancellationToken cancellationToken = default)
    {
        var photo = await db.CopyPhotos.FirstOrDefaultAsync(item => item.Id == photoId && item.BookId == bookId, cancellationToken);
        if (photo is null)
        {
            return false;
        }

        db.CopyPhotos.Remove(photo);
        await db.SaveChangesAsync(cancellationToken);
        covers.Delete(photo.StoredName);
        return true;
    }

    // The pictures' files, for when their book goes.
    public static async Task<List<string>> StoredForAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default) =>
        await db.CopyPhotos.AsNoTracking().Where(photo => photo.BookId == bookId).Select(photo => photo.StoredName).ToListAsync(cancellationToken);
}
