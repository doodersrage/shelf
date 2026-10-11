using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum ReadAlongState
{
    Waiting,
    Listening,
    Matching,
    Ready,
    Failed,
}

// A book's read-along: the words of its recording, heard by a speech-to-text service with when each was said, and
// the time each word of its EPUB is read aloud, by chapter. Made once for the files it was made from.
public sealed class ReadAlongRow
{
    public int BookId { get; set; }
    public ReadAlongState State { get; set; }

    // Pieces of the recording heard so far, of how many.
    public int Done { get; set; }
    public int Total { get; set; }
    public string? Problem { get; set; }
    public string? AudioStoredName { get; set; }
    public string? EbookStoredName { get; set; }

    // The words heard, and the times found for the book's words, each as gzipped JSON.
    public byte[]? Transcript { get; set; }
    public byte[]? Timings { get; set; }

    // How much of the book's text found a time, from 0 to 1.
    public double Matched { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record SpokenWord(string Text, double Start, double End);

public sealed record ReadAlongChapter(int Index, double Start, double End);

public sealed record ReadAlongStatus(ReadAlongState? State, int Done, int Total, string? Problem, double Matched, ReadAlongChapter[] Chapters, bool CanMake);

// The time each word of a chapter is read aloud, in hundredths of a second from the start of the recording; -1 for a
// word the recording does not read.
public sealed record ReadAlongTimes(int Chapter, int[] Times);

public interface ITranscriber
{
    bool Ready { get; }

    // The words in one audio file, with their times from its start; null when the service could not hear it.
    Task<IReadOnlyList<SpokenWord>?> TranscribeAsync(string path, string? language, CancellationToken cancellationToken);
}

// Any speech-to-text service with OpenAI's transcription API and word times: OpenAI's own, or one run at home, such
// as Speaches (faster-whisper), LocalAI, or whisper.cpp's server. ReadAlong:Url is the whole address of its
// transcriptions endpoint; ReadAlong:Model and ReadAlong:ApiKey as that service wants them.
public sealed class OpenAiTranscriber(HttpClient http, IConfiguration configuration) : ITranscriber
{
    public bool Ready => Uri.TryCreate(configuration["ReadAlong:Url"], UriKind.Absolute, out var url) && url.Scheme is "http" or "https";

    public async Task<IReadOnlyList<SpokenWord>?> TranscribeAsync(string path, string? language, CancellationToken cancellationToken)
    {
        if (!Ready)
        {
            return null;
        }

        await using var file = File.OpenRead(path);
        using var form = new MultipartFormDataContent
        {
            { new StreamContent(file) { Headers = { ContentType = new MediaTypeHeaderValue(AudioStore.ContentType(path)) } }, "file", Path.GetFileName(path) },
            { new StringContent(configuration["ReadAlong:Model"] ?? "whisper-1"), "model" },
            { new StringContent("verbose_json"), "response_format" },
            { new StringContent("word"), "timestamp_granularities[]" },
        };
        if (language is { Length: 2 })
        {
            form.Add(new StringContent(language), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, configuration["ReadAlong:Url"]) { Content = form };
        if (configuration["ReadAlong:ApiKey"] is { Length: > 0 } key)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        try
        {
            using var answer = await http.SendAsync(request, cancellationToken);
            if (!answer.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = await JsonDocument.ParseAsync(await answer.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return Read(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // Words at the top, as OpenAI gives them, or inside each segment, as some others do.
    public static IReadOnlyList<SpokenWord> Read(JsonElement root)
    {
        static IEnumerable<SpokenWord> Words(JsonElement list) => list.ValueKind != JsonValueKind.Array ? [] : list.EnumerateArray()
            .Where(word => word.TryGetProperty("start", out _) && word.TryGetProperty("end", out _))
            .Select(word => new SpokenWord(
                (word.TryGetProperty("word", out var text) ? text.GetString() : word.TryGetProperty("text", out var other) ? other.GetString() : null) ?? "",
                word.GetProperty("start").GetDouble(),
                word.GetProperty("end").GetDouble()));

        if (root.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array && words.GetArrayLength() > 0)
        {
            return Words(words).ToList();
        }

        return root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array
            ? segments.EnumerateArray().SelectMany(segment => segment.TryGetProperty("words", out var inner) ? Words(inner) : []).ToList()
            : [];
    }
}

// Cuts a long track into pieces a speech-to-text service takes, with ffmpeg (ReadAlong:FFmpeg) when it is there.
public interface IAudioCutter
{
    bool Available { get; }

    // A piece of a track, as a small mono MP3 in a temporary file; null when it could not be made.
    Task<string?> CutAsync(string track, double start, double length, CancellationToken cancellationToken);
}

public sealed class FfmpegCutter(IConfiguration configuration) : IAudioCutter
{
    private bool? available;

    private string Tool => configuration["ReadAlong:FFmpeg"] ?? "ffmpeg";

    public bool Available => available ??= Run(["-version"], TimeSpan.FromSeconds(10)) == 0;

    public async Task<string?> CutAsync(string track, double start, double length, CancellationToken cancellationToken)
    {
        var output = Path.Combine(Path.GetTempPath(), $"shelf-piece-{Guid.NewGuid():N}.mp3");
        var code = await Task.Run(() => Run(
            ["-nostdin", "-v", "error", "-y", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-t", length.ToString("0.###", CultureInfo.InvariantCulture),
             "-i", track, "-vn", "-ac", "1", "-ar", "16000", "-b:a", "48k", "-f", "mp3", output], TimeSpan.FromMinutes(10)), cancellationToken);
        if (code == 0 && File.Exists(output))
        {
            return output;
        }

        File.Delete(output);
        return null;
    }

    private int Run(IReadOnlyList<string> arguments, TimeSpan limit)
    {
        try
        {
            var start = new ProcessStartInfo(Tool) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return -1;
            }

            process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(limit))
            {
                process.Kill(entireProcessTree: true);
                return -1;
            }

            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}

public static partial class ReadAlongText
{
    // Words as the reader's page counts them: runs of letters, marks, and digits, with apostrophes inside, never
    // across a tag. The page counts the same way in each of its text nodes, so a word's number here is its number there.
    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+(?:['’][\p{L}\p{M}\p{N}]+)*")]
    public static partial Regex Word();

    [GeneratedRegex(@"<head[\s>].*?</head>|<script[\s>].*?</script>|<style[\s>].*?</style>|<!--.*?-->", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Hidden();

    [GeneratedRegex("<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tag();

    // The text of a chapter as its page's body holds it, with a break wherever a tag was.
    public static IReadOnlyList<string> Words(string html)
    {
        var body = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase) is var at and >= 0 ? html[at..] : html;
        var text = WebUtility.HtmlDecode(Tag().Replace(Hidden().Replace(body, ""), " "));
        return Word().Matches(text).Select(match => match.Value).ToList();
    }

    // How two words are compared: by their letters, without case, accents, or apostrophes.
    public static string Simple(string word)
    {
        var decomposed = word.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var kept = new StringBuilder(decomposed.Length);
        foreach (var letter in decomposed)
        {
            if (char.IsLetterOrDigit(letter))
            {
                kept.Append(letter);
            }
        }

        return kept.ToString();
    }
}

// Matching what was heard to what is written. Runs of words found once on both sides, and in the same order, pin the
// two together, the longest runs first and shorter ones in the gaps between; words between pins take times spread
// evenly. Text the recording skips (notes, an index) finds no time, and neither does what comes before the first pin
// or after the last.
public static class ReadAlongMatch
{
    public static int[] Align(IReadOnlyList<string> book, IReadOnlyList<SpokenWord> spoken)
    {
        var written = book.Select(ReadAlongText.Simple).ToArray();
        var heard = new List<(string Word, double Start)>();
        foreach (var word in spoken)
        {
            // A heard word may hold several written ones ("self-made"): they share its time.
            var parts = ReadAlongText.Word().Matches(word.Text).Select(match => ReadAlongText.Simple(match.Value)).Where(part => part.Length > 0).ToList();
            for (var i = 0; i < parts.Count; i++)
            {
                heard.Add((parts[i], word.Start + ((word.End - word.Start) * i / parts.Count)));
            }
        }

        var said = heard.Select(item => item.Word).ToArray();
        var pairs = new SortedDictionary<int, int>();
        var gaps = new Stack<(int BookFrom, int BookTo, int HeardFrom, int HeardTo, int Run)>();
        gaps.Push((0, written.Length, 0, said.Length, 6));
        while (gaps.Count > 0)
        {
            var (bookFrom, bookTo, heardFrom, heardTo, run) = gaps.Pop();
            if (bookTo - bookFrom < run || heardTo - heardFrom < run)
            {
                if (run > 1)
                {
                    gaps.Push((bookFrom, bookTo, heardFrom, heardTo, Math.Min(run - 1, Math.Min(bookTo - bookFrom, heardTo - heardFrom))));
                }

                continue;
            }

            var found = Pins(written, bookFrom, bookTo, said, heardFrom, heardTo, run);
            if (found.Count == 0)
            {
                if (run > 1)
                {
                    gaps.Push((bookFrom, bookTo, heardFrom, heardTo, run - 1));
                }

                continue;
            }

            var (lastBook, lastHeard) = (bookFrom, heardFrom);
            foreach (var (b, h) in found)
            {
                for (var k = 0; k < run; k++)
                {
                    pairs[b + k] = h + k;
                }

                if (run > 1)
                {
                    gaps.Push((lastBook, b, lastHeard, h, run - 1));
                }

                (lastBook, lastHeard) = (b + run, h + run);
            }

            if (run > 1)
            {
                gaps.Push((lastBook, bookTo, lastHeard, heardTo, run - 1));
            }
        }

        // Equal words next to pins join them, forward and back, up to the pins around them.
        var ordered = pairs.ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (b, h) = (ordered[i].Key, ordered[i].Value);
            var (toBook, toHeard) = i + 1 < ordered.Count ? (ordered[i + 1].Key, ordered[i + 1].Value) : (written.Length, said.Length);
            var (nb, nh) = (b + 1, h + 1);
            for (; nb < toBook && nh < toHeard && written[nb] == said[nh]; nb++, nh++)
            {
                pairs[nb] = nh;
            }

            var (fromBook, fromHeard) = i > 0 ? (ordered[i - 1].Key, ordered[i - 1].Value) : (-1, -1);
            for (var (pb, ph) = (b - 1, h - 1); pb > fromBook && ph > fromHeard && !pairs.ContainsKey(pb) && written[pb] == said[ph]; pb--, ph--)
            {
                pairs[pb] = ph;
            }
        }

        var times = Enumerable.Repeat(-1, written.Length).ToArray();
        var pinned = pairs.ToList();
        for (var i = 0; i < pinned.Count; i++)
        {
            var (b, h) = (pinned[i].Key, pinned[i].Value);
            times[b] = Hundredths(heard[h].Start);
            if (i + 1 >= pinned.Count)
            {
                break;
            }

            var (nextB, nextH) = (pinned[i + 1].Key, pinned[i + 1].Value);
            var bookGap = nextB - b;
            var heardGap = nextH - h;
            // Many written words with few heard between them were not read aloud.
            if (bookGap > 40 && heardGap * 4 < bookGap)
            {
                continue;
            }

            for (var k = 1; k < bookGap; k++)
            {
                times[b + k] = Hundredths(heard[h].Start + ((heard[nextH].Start - heard[h].Start) * k / bookGap));
            }
        }

        return times;
    }

    // Runs of words found once in each range, kept in the order they are in on both sides (the longest such chain).
    private static List<(int Book, int Heard)> Pins(string[] written, int bookFrom, int bookTo, string[] said, int heardFrom, int heardTo, int run)
    {
        static Dictionary<string, int> Once(string[] words, int from, int to, int run)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = from; i + run <= to; i++)
            {
                var key = string.Join(' ', words, i, run);
                seen[key] = seen.ContainsKey(key) ? -1 : i;
            }

            return seen;
        }

        var inBook = Once(written, bookFrom, bookTo, run);
        var inHeard = Once(said, heardFrom, heardTo, run);
        var candidates = inBook.Where(pair => pair.Value >= 0 && pair.Key.Length > 0 && inHeard.TryGetValue(pair.Key, out var h) && h >= 0)
            .Select(pair => (Book: pair.Value, Heard: inHeard[pair.Key]))
            .OrderBy(pair => pair.Book)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        // The longest chain rising on both sides, then runs that would overlap are let go.
        var tails = new List<int>();
        var previous = new int[candidates.Count];
        var tailIndex = new List<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var heard = candidates[i].Heard;
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (tails[mid] < heard)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            previous[i] = lo > 0 ? tailIndex[lo - 1] : -1;
            if (lo == tails.Count)
            {
                tails.Add(heard);
                tailIndex.Add(i);
            }
            else
            {
                tails[lo] = heard;
                tailIndex[lo] = i;
            }
        }

        var chain = new List<(int Book, int Heard)>();
        for (var i = tailIndex[^1]; i >= 0; i = previous[i])
        {
            chain.Add(candidates[i]);
        }

        chain.Reverse();
        var kept = new List<(int Book, int Heard)>();
        foreach (var pin in chain)
        {
            if (kept.Count == 0 || (pin.Book >= kept[^1].Book + run && pin.Heard >= kept[^1].Heard + run))
            {
                kept.Add(pin);
            }
        }

        return kept;
    }

    private static int Hundredths(double seconds) => (int)Math.Round(Math.Max(0, seconds) * 100);
}

// Makes read-alongs one book at a time, in the background, picking up where it was after a restart.
public sealed class ReadAlongService(IServiceScopeFactory scopes, ITranscriber transcriber, IAudioCutter cutter, AudioStore audio, EbookStore ebooks, IConfiguration configuration, ILogger<ReadAlongService> logger) : BackgroundService
{
    // Pieces of ten minutes each, when ffmpeg can cut them.
    public const double PieceSeconds = 600;
    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>();

    public bool Enabled => transcriber.Ready;

    // A service with a cap on uploads (OpenAI's is 25 MB) needs pieces cut small.
    private long MostBytes => configuration.GetValue<long>("ReadAlong:MaxUploadMegabytes", (configuration["ReadAlong:Url"] ?? "").Contains("api.openai.com", StringComparison.OrdinalIgnoreCase) ? 24 : 0) * 1024 * 1024;

    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/{id:int}/read-along", Status);
        books.MapGet("/{id:int}/read-along/chapters/{chapter:int}", Times);
        books.MapPost("/{id:int}/read-along", async Task<Results<Ok<ReadAlongStatus>, NotFound, ValidationProblem>> (int id, ShelfDb db, ReadAlongService service, CancellationToken cancellationToken) =>
            await service.StartAsync(db, id, cancellationToken) is { } problem
                ? problem.Length == 0 ? TypedResults.NotFound() : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["readAlong"] = [problem] })
                : TypedResults.Ok(await service.StatusAsync(db, id, cancellationToken)));
        books.MapDelete("/{id:int}/read-along", async Task<Results<NoContent, NotFound>> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await RemoveAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
    }

    private static async Task<Results<Ok<ReadAlongStatus>, NotFound>> Status(int id, ShelfDb db, ReadAlongService service, CancellationToken cancellationToken) =>
        await Lending.OpenAsync(db, id, cancellationToken) is null ? TypedResults.NotFound() : TypedResults.Ok(await service.StatusAsync(db, id, cancellationToken));

    private static async Task<Results<Ok<ReadAlongTimes>, NotFound>> Times(int id, int chapter, ShelfDb db, CancellationToken cancellationToken) =>
        await TimesAsync(db, id, chapter, cancellationToken) is { } times ? TypedResults.Ok(times) : TypedResults.NotFound();

    public async Task<ReadAlongStatus> StatusAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var open = await Lending.OpenAsync(db, id, cancellationToken);
        var row = open is null ? null : await db.ReadAlongs.AsNoTracking().FirstOrDefaultAsync(item => item.BookId == id, cancellationToken);
        var canMake = Enabled && open is { Borrowed: false } && EbookStore.IsEpub(open.Book.EbookStoredName) && !string.IsNullOrWhiteSpace(open.Book.AudioStoredName);
        if (row is null || open is null)
        {
            return new ReadAlongStatus(null, 0, 0, null, 0, [], canMake);
        }

        // Made from other files than the book has now: it no longer fits.
        var current = row.AudioStoredName == open.Book.AudioStoredName && row.EbookStoredName == open.Book.EbookStoredName;
        var chapters = row.State == ReadAlongState.Ready && current && Unpack<int[][]>(row.Timings) is { } timings
            ? timings.Select((times, index) => (Index: index, Times: times.Where(time => time >= 0).ToArray()))
                .Where(item => item.Times.Length > 0)
                .Select(item => new ReadAlongChapter(item.Index, item.Times.Min() / 100.0, item.Times.Max() / 100.0)).ToArray()
            : [];
        return new ReadAlongStatus(current ? row.State : null, row.Done, row.Total, row.Problem, row.Matched, chapters, canMake);
    }

    public static async Task<ReadAlongTimes?> TimesAsync(ShelfDb db, int id, int chapter, CancellationToken cancellationToken = default)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open)
        {
            return null;
        }

        var row = await db.ReadAlongs.AsNoTracking().FirstOrDefaultAsync(item => item.BookId == id && item.State == ReadAlongState.Ready, cancellationToken);
        if (row is null || row.AudioStoredName != open.Book.AudioStoredName || row.EbookStoredName != open.Book.EbookStoredName || Unpack<int[][]>(row.Timings) is not { } timings
            || chapter < 0 || chapter >= timings.Length)
        {
            return null;
        }

        return new ReadAlongTimes(chapter, timings[chapter]);
    }

    // Where a chapter's place (0 to 1 through it) is heard, in seconds from the start of the recording; null when not known.
    public static async Task<double?> SecondsAtAsync(ShelfDb db, int id, int chapter, double within, CancellationToken cancellationToken = default)
    {
        if (await TimesAsync(db, id, chapter, cancellationToken) is not { Times.Length: > 0 } found)
        {
            return null;
        }

        var start = (int)Math.Clamp(Math.Round(within * (found.Times.Length - 1)), 0, found.Times.Length - 1);
        var time = found.Times.Skip(start).FirstOrDefault(value => value >= 0, -1);
        time = time >= 0 ? time : found.Times.Take(start).LastOrDefault(value => value >= 0, -1);
        return time >= 0 ? time / 100.0 : null;
    }

    // The chapter, and how far through it, read at a moment of the recording; null when not known.
    public static async Task<PartSpot?> SpotAtAsync(ShelfDb db, int id, double seconds, CancellationToken cancellationToken = default)
    {
        if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open)
        {
            return null;
        }

        var row = await db.ReadAlongs.AsNoTracking().FirstOrDefaultAsync(item => item.BookId == id && item.State == ReadAlongState.Ready, cancellationToken);
        if (row is null || row.AudioStoredName != open.Book.AudioStoredName || row.EbookStoredName != open.Book.EbookStoredName || Unpack<int[][]>(row.Timings) is not { } timings)
        {
            return null;
        }

        var wanted = (int)Math.Round(seconds * 100);
        (int Chapter, int Word, int Time)? best = null;
        for (var chapter = 0; chapter < timings.Length; chapter++)
        {
            for (var word = 0; word < timings[chapter].Length; word++)
            {
                var time = timings[chapter][word];
                if (time >= 0 && time <= wanted && (best is null || time >= best.Value.Time))
                {
                    best = (chapter, word, time);
                }
            }
        }

        return best is { } found ? new PartSpot(found.Chapter, timings[found.Chapter].Length > 1 ? found.Word / (double)(timings[found.Chapter].Length - 1) : 0) : null;
    }

    // Starts making one for a book of the reader's own, with an EPUB and a recording. Null when it is under way; an
    // empty problem when there is no such book.
    public async Task<string?> StartAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return "";
        }

        if (!Enabled)
        {
            return T("This shelf has no speech-to-text service set up for read-along. An admin can add one in ReadAlong:Url.");
        }

        if (!EbookStore.IsEpub(book.EbookStoredName) || string.IsNullOrWhiteSpace(book.AudioStoredName))
        {
            return T("Read-along needs both an EPUB and an audiobook on the book.");
        }

        var row = await db.ReadAlongs.FirstOrDefaultAsync(item => item.BookId == id, cancellationToken);
        if (row is null)
        {
            row = new ReadAlongRow { BookId = id };
            db.ReadAlongs.Add(row);
        }

        // The words heard can be kept when only the e-book changed; the recording must be heard again when it did.
        var sameRecording = row.AudioStoredName == book.AudioStoredName && row.Transcript is not null && row.Done >= row.Total && row.Total > 0;
        (row.State, row.Problem, row.EbookStoredName, row.Timings, row.Matched, row.UpdatedAt) = (sameRecording ? ReadAlongState.Matching : ReadAlongState.Waiting, null, book.EbookStoredName, null, 0, DateTimeOffset.UtcNow);
        if (!sameRecording)
        {
            (row.AudioStoredName, row.Transcript, row.Done, row.Total) = (book.AudioStoredName, null, 0, 0);
        }

        await db.SaveChangesAsync(cancellationToken);
        _queue.Writer.TryWrite(id);
        return null;
    }

    public static async Task<bool> RemoveAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        if (!await db.Books.AnyAsync(book => book.Id == id, cancellationToken))
        {
            return false;
        }

        await db.ReadAlongs.Where(item => item.BookId == id).ExecuteDeleteAsync(cancellationToken);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            foreach (var id in await db.ReadAlongs.AsNoTracking().Where(item => item.State != ReadAlongState.Ready && item.State != ReadAlongState.Failed).Select(item => item.BookId).ToListAsync(stoppingToken))
            {
                _queue.Writer.TryWrite(id);
            }
        }

        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await MakeAsync(id, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not make the read-along for book {Book}.", id);
                await FailAsync(id, T("Something went wrong making it. Try again."), stoppingToken);
            }
        }
    }

    // Hears the recording piece by piece, keeping what was heard after each, then matches it to the text.
    public async Task MakeAsync(int id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var row = await db.ReadAlongs.FirstOrDefaultAsync(item => item.BookId == id, cancellationToken);
        var book = await db.Books.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is null || book is null || row.State is ReadAlongState.Ready or ReadAlongState.Failed)
        {
            return;
        }

        if (book.AudioStoredName != row.AudioStoredName || book.EbookStoredName != row.EbookStoredName || ebooks.OpenPath(book.EbookStoredName) is not { } epub)
        {
            await FailAsync(id, T("The book's files changed while it was being made. Make it again."), cancellationToken);
            return;
        }

        var pieces = Pieces(book.AudioStoredName);
        if (pieces is null)
        {
            await FailAsync(id, T("A track is too large to send to the speech-to-text service, and there is no ffmpeg here to cut it smaller."), cancellationToken);
            return;
        }

        var heard = Unpack<List<SpokenWord>>(row.Transcript) ?? [];
        (row.State, row.Total) = (ReadAlongState.Listening, pieces.Count);
        await db.SaveChangesAsync(cancellationToken);
        var language = WordLookups.LanguageOf(book.Language);
        for (var i = row.Done; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            var cut = piece.Length is double length ? await cutter.CutAsync(piece.Track, piece.Start, length, cancellationToken) : piece.Track;
            if (cut is null)
            {
                await FailAsync(id, T("ffmpeg could not cut the recording into pieces."), cancellationToken);
                return;
            }

            IReadOnlyList<SpokenWord>? words;
            try
            {
                words = await transcriber.TranscribeAsync(cut, language, cancellationToken);
            }
            finally
            {
                if (cut != piece.Track)
                {
                    File.Delete(cut);
                }
            }

            if (words is null)
            {
                await FailAsync(id, T("The speech-to-text service did not answer, or did not give word times. It will need trying again."), cancellationToken);
                return;
            }

            heard.AddRange(words.Select(word => word with { Start = word.Start + piece.Offset, End = word.End + piece.Offset }));
            (row.Done, row.Transcript, row.UpdatedAt) = (i + 1, Pack(heard), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }

        row.State = ReadAlongState.Matching;
        await db.SaveChangesAsync(cancellationToken);

        var count = EpubFile.Chapters(epub)?.Count ?? 0;
        var chapters = Enumerable.Range(0, count).Select(index => EpubFile.ChapterHtml(epub, index, "") is { } html ? ReadAlongText.Words(html) : []).ToList();
        var all = chapters.SelectMany(words => words).ToList();
        var times = await Task.Run(() => ReadAlongMatch.Align(all, heard), cancellationToken);
        var timings = new int[count][];
        var at = 0;
        for (var c = 0; c < count; c++)
        {
            timings[c] = times[at..(at + chapters[c].Count)];
            at += chapters[c].Count;
        }

        (row.Timings, row.Matched, row.State, row.UpdatedAt) = (Pack(timings), all.Count == 0 ? 0 : times.Count(time => time >= 0) / (double)all.Count, ReadAlongState.Ready, DateTimeOffset.UtcNow);
        if (row.Matched < 0.05)
        {
            (row.State, row.Problem) = (ReadAlongState.Failed, T("The recording and the e-book hardly match. Are they the same book, in the same language?"));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // The pieces to hear: each track whole, or cut every ten minutes when ffmpeg is here. Null when a track is too
    // large for the service and cannot be cut.
    private List<(string Track, double Offset, double Start, double? Length)>? Pieces(string? storedName)
    {
        var lengths = audio.Lengths(storedName);
        var pieces = new List<(string, double, double, double?)>();
        var offset = 0.0;
        foreach (var track in audio.Tracks(storedName))
        {
            var path = audio.TrackPath(storedName, track.Index)!;
            var length = lengths[track.Index];
            if (cutter.Available && length is double known && known > 0)
            {
                for (var start = 0.0; start < known; start += PieceSeconds)
                {
                    pieces.Add((path, offset + start, start, Math.Min(PieceSeconds, known - start)));
                }
            }
            else if (MostBytes > 0 && new FileInfo(path).Length > MostBytes)
            {
                return null;
            }
            else
            {
                pieces.Add((path, offset, 0, null));
            }

            offset += length ?? 0;
        }

        return pieces;
    }

    private async Task FailAsync(int id, string problem, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        await db.ReadAlongs.Where(item => item.BookId == id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.State, ReadAlongState.Failed).SetProperty(item => item.Problem, problem).SetProperty(item => item.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
    }

    private static byte[] Pack<T>(T value)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            JsonSerializer.Serialize(zip, value);
        }

        return output.ToArray();
    }

    private static T? Unpack<T>(byte[]? packed)
    {
        if (packed is null)
        {
            return default;
        }

        try
        {
            using var zip = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
            return JsonSerializer.Deserialize<T>(zip);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return default;
        }
    }
}
