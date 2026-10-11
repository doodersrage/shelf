using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum M4bState
{
    None,
    Making,
    Ready,
    Failed,
}

public sealed record M4bStatus(M4bState State, bool CanMake, long? Size, string? Problem);

// A recording in many tracks made into one M4B with its chapters and cover, by ffmpeg (Audio:FFmpeg): to download
// for another player, or to keep as the book's recording in place of the tracks. Made in the background, one at a time.
public sealed class M4bService(IServiceScopeFactory scopes, AudioStore audio, CoverStore covers, EbookStore ebooks, IConfiguration configuration, ILogger<M4bService> logger) : BackgroundService
{
    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>();
    private readonly ConcurrentDictionary<string, string?> _making = new();
    private bool? _available;

    private string Tool => configuration["Audio:FFmpeg"] ?? configuration["ReadAlong:FFmpeg"] ?? "ffmpeg";

    // Asked once, off the page's own thread: waiting there on the answer could keep it from ever coming.
    public bool Available => _available ??= Task.Run(() => Tools.RunAsync(Tool, ["-version"], TimeSpan.FromSeconds(10), CancellationToken.None)).GetAwaiter().GetResult() is not null;

    private string Root => Path.Combine(audio.Root, "m4b");

    // The recordings being made into an M4B now, by their stored names, for the Activity page.
    public IReadOnlyCollection<string> Making => [.. _making.Where(item => item.Value is null).Select(item => item.Key)];

    public string PathFor(string storedName) => Path.Combine(Root, storedName + ".m4b");

    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/audio/m4b", async Task<Results<Ok<M4bStatus>, NotFound>> (int id, ShelfDb db, M4bService service, CancellationToken cancellationToken) =>
            await service.StatusAsync(db, id, cancellationToken) is { } status ? TypedResults.Ok(status) : TypedResults.NotFound());
        books.MapPost("/{id:int}/audio/m4b", async Task<Results<Ok<M4bStatus>, NotFound, ValidationProblem>> (int id, ShelfDb db, M4bService service, CancellationToken cancellationToken) =>
            await service.StartAsync(db, id, cancellationToken) switch
            {
                null => TypedResults.Ok((await service.StatusAsync(db, id, cancellationToken))!),
                "" => TypedResults.NotFound(),
                var problem => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["m4b"] = [problem] }),
            });
        books.MapGet("/{id:int}/audio/m4b/file", async Task<IResult> (int id, ShelfDb db, M4bService service, CancellationToken cancellationToken) =>
            await Lending.OpenAsync(db, id, cancellationToken) is { } open && open.Book.AudioStoredName is { } stored && File.Exists(service.PathFor(stored))
                ? Results.File(service.PathFor(stored), "audio/mp4", $"{Safe(open.Book.Author)} - {Safe(open.Book.Title)}.m4b", enableRangeProcessing: true)
                : Results.NotFound());
        books.MapPost("/{id:int}/audio/m4b/use", async Task<Results<NoContent, NotFound>> (int id, ShelfDb db, M4bService service, CancellationToken cancellationToken) =>
            await service.UseAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
    }

    private static string Safe(string text) => new(text.Where(letter => !Path.GetInvalidFileNameChars().Contains(letter)).ToArray());

    public async Task<M4bStatus?> StatusAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open)
        {
            return null;
        }

        var stored = open.Book.AudioStoredName;
        var canMake = !open.Borrowed && stored is not null && audio.Tracks(stored).Count > 1 && Available;
        if (stored is null)
        {
            return new M4bStatus(M4bState.None, false, null, null);
        }

        if (_making.TryGetValue(stored, out var problem))
        {
            return new M4bStatus(problem is null ? M4bState.Making : M4bState.Failed, canMake, null, problem);
        }

        var path = PathFor(stored);
        return File.Exists(path) ? new M4bStatus(M4bState.Ready, canMake, new FileInfo(path).Length, null) : new M4bStatus(M4bState.None, canMake, null, null);
    }

    // Null when it is under way; an empty problem when there is no such book of the reader's.
    public async Task<string?> StartAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book?.AudioStoredName is not { } stored)
        {
            return "";
        }

        if (!Available)
        {
            return T("Making one M4B needs ffmpeg on the server.");
        }

        if (audio.Tracks(stored).Count < 2)
        {
            return T("The recording is one file already.");
        }

        if (_making.TryGetValue(stored, out var problem) && problem is null)
        {
            return null;
        }

        _making[stored] = null;
        _queue.Writer.TryWrite(id);
        return null;
    }

    // The M4B in place of the tracks, keeping the place in the recording.
    public async Task<bool> UseAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book?.AudioStoredName is not { } stored || !File.Exists(PathFor(stored)))
        {
            return false;
        }

        var lengths = audio.Lengths(stored);
        var seconds = lengths.Take(book.AudioTrack ?? 0).Sum(length => length ?? 0) + (book.AudioSeconds ?? 0);
        AudioSave saved;
        await using (var file = File.OpenRead(PathFor(stored)))
        {
            saved = await audio.SaveAsync([new AudioUpload(Path.GetFileNameWithoutExtension(book.AudioFileName ?? "book") + ".m4b", file, file.Length)], cancellationToken);
        }

        if (saved.StoredName is null)
        {
            return false;
        }

        File.Delete(PathFor(stored));
        await AudioEndpoints.AttachAsync(db, audio, covers, book, saved.StoredName, saved.FileName ?? "book.m4b", cancellationToken);
        (book.AudioTrack, book.AudioSeconds, book.AudioChapterNames) = (0, (int)seconds, null);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await MakeAsync(id, stoppingToken);
        }
    }

    // Returns the stored name it was made for. The chapters are the player's: Audible's or set by hand, else the
    // files' own marks, else one a track.
    private async Task<string?> MakeAsync(int id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var book = await db.Books.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book?.AudioStoredName is not { } stored)
        {
            return null;
        }

        var tracks = audio.Tracks(stored);
        var lengths = audio.Lengths(stored);
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-m4b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var list = new StringBuilder();
            foreach (var track in tracks)
            {
                list.Append("file '").Append(audio.TrackPath(stored, track.Index)!.Replace("'", "'\\''")).Append("'\n");
            }

            await File.WriteAllTextAsync(Path.Combine(folder, "tracks.txt"), list.ToString(), cancellationToken);
            var chapters = Audible.Place(book.AudioChapterNames, lengths) is { } placed
                ? placed.Select(chapter => (chapter.Title, Start: lengths.Take(chapter.Track).Sum(length => length ?? 0) + chapter.Start)).ToList()
                : tracks.Select(track => (track.Title, Start: lengths.Take(track.Index).Sum(length => length ?? 0))).ToList();
            var total = lengths.Sum(length => length ?? 0);
            var metadata = new StringBuilder(";FFMETADATA1\n")
                .Append("title=").Append(Escape(book.Title)).Append('\n')
                .Append("artist=").Append(Escape(book.Author)).Append('\n')
                .Append("album=").Append(Escape(book.Title)).Append('\n');
            if (book.Narrator is { } narrator)
            {
                metadata.Append("composer=").Append(Escape(narrator)).Append('\n');
            }

            for (var i = 0; i < chapters.Count; i++)
            {
                var end = i + 1 < chapters.Count ? chapters[i + 1].Start : total;
                metadata.Append("[CHAPTER]\nTIMEBASE=1/1000\n")
                    .Append("START=").Append((long)(chapters[i].Start * 1000)).Append('\n')
                    .Append("END=").Append((long)(Math.Max(end, chapters[i].Start + 1) * 1000)).Append('\n')
                    .Append("title=").Append(Escape(chapters[i].Title)).Append('\n');
            }

            await File.WriteAllTextAsync(Path.Combine(folder, "chapters.txt"), metadata.ToString(), cancellationToken);
            var output = Path.Combine(folder, "book.m4b");
            List<string> arguments = ["-nostdin", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", Path.Combine(folder, "tracks.txt"), "-i", Path.Combine(folder, "chapters.txt")];
            var cover = Covers.Picture(book, ebooks, covers);
            if (cover is not null)
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, "cover.jpg"), cover, cancellationToken);
                arguments.AddRange(["-i", Path.Combine(folder, "cover.jpg"), "-map", "0:a", "-map", "2:v", "-c:v", "mjpeg", "-disposition:v", "attached_pic"]);
            }
            else
            {
                arguments.AddRange(["-map", "0:a"]);
            }

            arguments.AddRange(["-map_metadata", "1", "-map_chapters", "1", "-c:a", "aac", "-b:a", configuration["Audio:M4bBitrate"] ?? "64k", "-movflags", "+faststart", "-f", "mp4", output]);
            if (await Tools.RunAsync(Tool, arguments, TimeSpan.FromHours(6), cancellationToken) is null || !File.Exists(output))
            {
                _making[stored] = T("ffmpeg could not make the M4B.");
                return stored;
            }

            Directory.CreateDirectory(Root);
            File.Move(output, PathFor(stored), overwrite: true);
            _making.TryRemove(stored, out _);
            return stored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not make an M4B for book {Book}.", id);
            _making[stored] = T("Something went wrong making it. Try again.");
            return stored;
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\n", " ");
}
