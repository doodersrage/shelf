using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// Casting the player to a Chromecast or AirPlay: the device fetches the tracks itself, without the reader's sign-in,
// so it's given addresses signed for twelve hours, for this reader and this book's recording only. Each fetch asks
// again, as that reader, whether the book still opens to them (theirs, lent, or shared) and its recording is the
// same. A household member who may not download gets none: an address that works without signing in is a download.
public sealed record CastPlan(string[] Tracks);

public static class CastLinks
{
    public static readonly TimeSpan Lasts = TimeSpan.FromHours(12);

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection) =>
        protection.CreateProtector("Shelf.Cast.Tracks").ToTimeLimitedDataProtector();

    public static void Map(IEndpointRouteBuilder app, RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/audio/cast", async Task<IResult> (int id, ShelfDb db, AudioStore audio, IDataProtectionProvider protection, CancellationToken cancellationToken) =>
        {
            if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open || open.Book.AudioStoredName is not { } stored)
            {
                return TypedResults.NotFound();
            }

            if (!await Households.MayDownloadAsync(db, open, cancellationToken))
            {
                return TypedResults.Forbid();
            }

            var token = Protector(protection).Protect($"{db.ReaderId}:{id}:{stored}", Lasts);
            return TypedResults.Ok(new CastPlan([.. audio.Tracks(stored).Select(track => $"/cast/{token}/{track.Index}")]));
        }).WithTags("Files");

        app.MapGet("/cast/{token}/{index:int}", async Task<IResult> (string token, int index, HttpContext http, ShelfDb db, Readers.ShelfReader reader, AudioStore audio, IDataProtectionProvider protection, CancellationToken cancellationToken) =>
        {
            string said;
            try
            {
                said = Protector(protection).Unprotect(token);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return Results.NotFound();
            }

            var parts = said.Split(':', 3);
            if (parts.Length != 3 || !int.TryParse(parts[0], out var readerId) || !int.TryParse(parts[1], out var bookId))
            {
                return Results.NotFound();
            }

            // As the reader the address was made for, the same question as when it was made.
            var stored = parts[2];
            reader.Use(readerId);
            if (await db.Readers.AsNoTracking().AnyAsync(item => item.Id == readerId && item.Suspended, cancellationToken)
                || await Lending.OpenAsync(db, bookId, cancellationToken) is not { } open
                || open.Book.AudioStoredName != stored
                || !await Households.MayDownloadAsync(db, open, cancellationToken)
                || audio.TrackPath(stored, index) is not { } path)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, no-store";
            return Results.File(path, AudioStore.ContentType(path), enableRangeProcessing: true);
        }).AllowAnonymous().ExcludeFromDescription();
    }
}
