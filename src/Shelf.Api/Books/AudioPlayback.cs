using Microsoft.AspNetCore.Http.HttpResults;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// Everything the player needs to play a book as one recording: its tracks and how long each runs, its chapters
// (the marks inside a single file, or else one for each track), where this reader stopped, and their speed.
public sealed record AudioPlan(
    int BookId,
    string Title,
    string Author,
    string? Cover,
    AudioPlanTrack[] Tracks,
    AudioPlanChapter[] Chapters,
    int Track,
    double Seconds,
    int Speed,
    bool HasEbook);

public sealed record AudioPlanTrack(int Index, string Title, double? Length, string Url);

// A chapter starts in a track, so many tracks or one long file are told the same way.
public sealed record AudioPlanChapter(int Index, string Title, int Track, double Start);

public sealed record AudioPlaceRequest(int Track, double Seconds);

public sealed record AudioSpeedRequest(int Speed);

public static class AudioPlayback
{
    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/audio/plan", Plan);
        books.MapPut("/{id:int}/audio/place", Place);
        books.MapPut("/audio/speed", Speed);
    }

    public static async Task<AudioPlan?> PlanAsync(ShelfDb db, AudioStore store, int id, CancellationToken cancellationToken = default)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open || string.IsNullOrWhiteSpace(open.Book.AudioStoredName))
        {
            return null;
        }

        var book = open.Book;
        var tracks = store.Tracks(book.AudioStoredName);
        if (tracks.Count == 0)
        {
            return null;
        }

        var lengths = store.Lengths(book.AudioStoredName);
        var marks = store.Chapters(book.AudioStoredName);
        var chapters = marks.Count > 1
            ? marks.Select(mark => new AudioPlanChapter(mark.Index, mark.Title, 0, mark.Start)).ToArray()
            : tracks.Select(track => new AudioPlanChapter(track.Index, track.Title, track.Index, 0)).ToArray();
        var place = await Lending.PlaceAsync(db, open, cancellationToken);
        var at = place.AudioTrack is int saved && saved >= 0 && saved < tracks.Count ? saved : 0;
        return new AudioPlan(
            book.Id,
            book.Title,
            book.Author,
            book.CoverShown,
            tracks.Select(track => new AudioPlanTrack(track.Index, track.Title, lengths[track.Index], $"/books/{book.Id}/audio/tracks/{track.Index}")).ToArray(),
            chapters,
            at,
            Math.Max(0, place.AudioSeconds ?? 0),
            await BookRules.GetAudioSpeedAsync(db, cancellationToken),
            EbookStore.IsEpub(book.EbookStoredName));
    }

    private static async Task<Results<Ok<AudioPlan>, NotFound>> Plan(int id, ShelfDb db, AudioStore store, CancellationToken cancellationToken) =>
        await PlanAsync(db, store, id, cancellationToken) is { } plan ? TypedResults.Ok(plan) : TypedResults.NotFound();

    // Where the player is: kept as it is, back or forward, since a listener moves both ways.
    private static async Task<Results<NoContent, NotFound, ValidationProblem>> Place(int id, AudioPlaceRequest request, ShelfDb db, AudioStore store, CancellationToken cancellationToken)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open || string.IsNullOrWhiteSpace(open.Book.AudioStoredName))
        {
            return TypedResults.NotFound();
        }

        if (request.Track < 0 || request.Track >= store.Tracks(open.Book.AudioStoredName).Count || request.Seconds is < 0 or > 1_000_000 || double.IsNaN(request.Seconds))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Track"] = [Localization.Words.T("Give a track of this recording, and a time in it.")] });
        }

        await Lending.KeepPlaceAsync(db, open, place =>
        {
            place.AudioTrack = request.Track;
            place.AudioSeconds = (int)request.Seconds;
        }, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> Speed(AudioSpeedRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        await BookRules.SetAudioSpeedAsync(db, request.Speed, cancellationToken);
        return TypedResults.NoContent();
    }
}
