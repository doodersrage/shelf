using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// Casting the player to a Chromecast or AirPlay: the device fetches the tracks itself, without the reader's sign-in,
// so it's given addresses signed for twelve hours, for this reader and this book's recording only. They stop when
// the book stops being the reader's, or its recording changes.
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

            var token = Protector(protection).Protect($"{db.ReaderId}:{id}:{stored}", Lasts);
            return TypedResults.Ok(new CastPlan([.. audio.Tracks(stored).Select(track => $"/cast/{token}/{track.Index}")]));
        }).WithTags("Files");

        app.MapGet("/cast/{token}/{index:int}", async Task<IResult> (string token, int index, ShelfDb db, AudioStore audio, IDataProtectionProvider protection, CancellationToken cancellationToken) =>
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

            var stored = parts[2];
            var open = await db.Books.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(book => book.Id == bookId && book.AudioStoredName == stored && (book.OwnerId == readerId || book.BorrowerId == readerId), cancellationToken)
                && !await db.Readers.AsNoTracking().AnyAsync(reader => reader.Id == readerId && reader.Suspended, cancellationToken);
            return open && audio.TrackPath(stored, index) is { } path
                ? Results.File(path, AudioStore.ContentType(path), enableRangeProcessing: true)
                : Results.NotFound();
        }).AllowAnonymous().ExcludeFromDescription();
    }
}
