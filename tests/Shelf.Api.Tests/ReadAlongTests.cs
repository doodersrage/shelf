using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class ReadAlongTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures"));

    [Fact]
    public void Heard_words_find_their_written_ones_despite_slips_and_skipped_text()
    {
        var random = new Random(7);
        var vocabulary = Enumerable.Range(0, 400).Select(i => "word" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        // Front matter, the story, notes nobody reads aloud, and more story.
        var front = Enumerable.Range(0, 30).Select(_ => vocabulary[random.Next(vocabulary.Length)]).ToList();
        var story = Enumerable.Range(0, 600).Select(_ => vocabulary[random.Next(vocabulary.Length)]).ToList();
        var notes = Enumerable.Range(0, 80).Select(_ => vocabulary[random.Next(vocabulary.Length)]).ToList();
        var more = Enumerable.Range(0, 300).Select(_ => vocabulary[random.Next(vocabulary.Length)]).ToList();
        var book = front.Concat(story).Concat(notes).Concat(more).ToList();

        var spoken = new List<SpokenWord>();
        var time = 5.0;
        var expected = new Dictionary<int, double>();
        void Say(IEnumerable<(string Word, int Index)> words)
        {
            foreach (var (word, index) in words)
            {
                // Now and then a word is misheard, or a word is said that is not written.
                var heard = random.NextDouble() < 0.05 ? "misheard" : word;
                if (heard == word)
                {
                    expected[index] = time;
                }

                spoken.Add(new SpokenWord(" " + heard + (random.NextDouble() < 0.1 ? "," : ""), time, time + 0.25));
                time += 0.3;
                if (random.NextDouble() < 0.02)
                {
                    spoken.Add(new SpokenWord(" um", time, time + 0.2));
                    time += 0.3;
                }
            }
        }

        Say(story.Select((word, i) => (word, front.Count + i)));
        Say(more.Select((word, i) => (word, front.Count + story.Count + notes.Count + i)));

        var times = ReadAlongMatch.Align(book, spoken);

        Assert.All(times.Take(front.Count), value => Assert.Equal(-1, value));
        Assert.All(times.Skip(front.Count + story.Count + 1).Take(notes.Count - 2), value => Assert.Equal(-1, value));
        var close = expected.Count(pair => times[pair.Key] >= 0 && Math.Abs((times[pair.Key] / 100.0) - pair.Value) < 0.05);
        Assert.True(close > expected.Count * 0.97, $"{close} of {expected.Count} found their time.");
    }

    [Fact]
    public void A_chapters_words_are_counted_as_its_page_counts_them()
    {
        var html = """<html><head><title>Not this</title><style>p{}</style></head><body><h1>Chapter&nbsp;1</h1><p>It’s a <b>dr</b>eam, isn't it? Café 42.</p><script>no()</script></body></html>""";
        // A tag always ends a word, as a text node does on the page, even inside one.
        Assert.Equal(["Chapter", "1", "It’s", "a", "dr", "eam", "isn't", "it", "Café", "42"], ReadAlongText.Words(html));
    }

    [Fact]
    public async Task A_book_is_heard_track_by_track_and_its_chapters_get_times()
    {
        var reader = await factory.SignUpAsync("Read Along Reader");
        var readerId = await factory.ReaderIdAsync("Read Along Reader");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Moby-Dick", "Herman Melville", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(File.OpenRead(Path.Combine(Fixtures, "moby.epub"))), "file", "moby.epub" } })
        {
            (await reader.PostAsync($"/books/{book.Id}/ebook", content)).EnsureSuccessStatusCode();
        }

        using (var content = new MultipartFormDataContent { { new StreamContent(File.OpenRead(Path.Combine(Fixtures, "three-tracks.zip"))), "file", "three-tracks.zip" } })
        {
            (await reader.PostAsync($"/books/{book.Id}/audio", content)).EnsureSuccessStatusCode();
        }

        // The stand-in hears the chapters' words, a third of the book in each track, spread over it.
        var epub = Path.Combine(Path.GetTempPath(), $"shelf-moby-{Guid.NewGuid():N}.epub");
        File.Copy(Path.Combine(Fixtures, "moby.epub"), epub);
        var chapters = Enumerable.Range(0, EpubFile.Chapters(epub)!.Count).Select(index => ReadAlongText.Words(EpubFile.ChapterHtml(epub, index, "")!)).ToList();
        File.Delete(epub);
        var all = chapters.SelectMany(words => words).ToList();
        factory.Transcriber.Words = all;

        var status = await (await reader.PostAsync($"/books/{book.Id}/read-along", null)).Content.ReadFromJsonAsync<ReadAlongStatus>(JsonOptions);
        Assert.NotNull(status);
        for (var i = 0; i < 100 && status!.State != ReadAlongState.Ready && status.State != ReadAlongState.Failed; i++)
        {
            await Task.Delay(100);
            status = await reader.GetFromJsonAsync<ReadAlongStatus>($"/books/{book.Id}/read-along", JsonOptions);
        }

        Assert.Equal((ReadAlongState.Ready, 3, 3), (status!.State, status.Done, status.Total));
        Assert.True(status.Matched > 0.9, $"Matched {status.Matched}.");
        Assert.Equal(3, factory.Transcriber.Heard.Count);

        var last = chapters.FindLastIndex(words => words.Count > 0);
        var times = await reader.GetFromJsonAsync<ReadAlongTimes>($"/books/{book.Id}/read-along/chapters/{last}", JsonOptions);
        Assert.Equal(chapters[last].Count, times!.Times.Length);
        Assert.True(times.Times.Where(time => time >= 0).SequenceEqual(times.Times.Where(time => time >= 0).Order()));

        // A place in the text and a moment of the recording, each from the other.
        await using (var scope = Scope(readerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var seconds = await ReadAlongService.SecondsAtAsync(db, book.Id, last, 0.5);
            Assert.NotNull(seconds);
            var spot = await ReadAlongService.SpotAtAsync(db, book.Id, seconds!.Value);
            Assert.Equal(last, spot!.Part);
            Assert.InRange(spot.Within, 0.45, 0.55);
        }

        // Others cannot start one on a book that is not theirs.
        var stranger = await factory.SignUpAsync("Read Along Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/books/{book.Id}/read-along", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/read-along/chapters/{last}")).StatusCode);
    }

    [Fact]
    public void The_transcriber_reads_word_times_at_the_top_or_inside_segments()
    {
        using var top = JsonDocument.Parse("""{"text":"Call me","words":[{"word":"Call","start":0.0,"end":0.4},{"word":"me","start":0.4,"end":0.6}]}""");
        Assert.Equal([new SpokenWord("Call", 0, 0.4), new SpokenWord("me", 0.4, 0.6)], OpenAiTranscriber.Read(top.RootElement));
        using var inside = JsonDocument.Parse("""{"segments":[{"words":[{"word":" Ishmael.","start":1.0,"end":1.5}]}]}""");
        Assert.Equal([new SpokenWord(" Ishmael.", 1, 1.5)], OpenAiTranscriber.Read(inside.RootElement));
    }

    private AsyncServiceScope Scope(int readerId)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope;
    }
}

// A speech-to-text service that hears, in each file it is sent, the next third of the words it is given.
public sealed class StubTranscriber : ITranscriber
{
    public bool Ready => true;

    public List<string> Words { get; set; } = [];

    public ConcurrentQueue<string> Heard { get; } = new();

    public Task<IReadOnlyList<SpokenWord>?> TranscribeAsync(string path, string? language, CancellationToken cancellationToken)
    {
        var part = Heard.Count;
        Heard.Enqueue(path);
        var length = AudioLength.Read(path) ?? 3;
        var share = Words.Skip(part * Words.Count / 3).Take(((part + 1) * Words.Count / 3) - (part * Words.Count / 3)).ToList();
        var step = length / Math.Max(1, share.Count);
        return Task.FromResult<IReadOnlyList<SpokenWord>?>(share.Select((word, i) => new SpokenWord(" " + word, i * step, (i + 0.9) * step)).ToList());
    }
}

public sealed class StubCutter : IAudioCutter
{
    public bool Available => false;

    public Task<string?> CutAsync(string track, double start, double length, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
