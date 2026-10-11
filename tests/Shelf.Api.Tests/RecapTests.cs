using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class RecapTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures"));

    [Fact]
    public async Task A_recap_covers_the_book_up_to_the_place_and_nothing_after()
    {
        var reader = await factory.SignUpAsync("Recap Reader");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Moby-Dick", "Herman Melville", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(File.OpenRead(Path.Combine(Fixtures, "moby.epub"))), "file", "moby.epub" } })
        {
            (await reader.PostAsync($"/books/{book.Id}/ebook", content)).EnsureSuccessStatusCode();
        }

        var epub = Path.Combine(Path.GetTempPath(), $"shelf-recap-{Guid.NewGuid():N}.epub");
        File.Copy(Path.Combine(Fixtures, "moby.epub"), epub);
        var chapters = Enumerable.Range(0, EpubFile.Chapters(epub)!.Count).Select(index => Search.PlainText(EpubFile.ChapterHtml(epub, index, "")!)).ToList();
        File.Delete(epub);
        var last = chapters.FindLastIndex(text => text.Length >= 200);
        var before = chapters.FindIndex(text => text.Length >= 200);
        Assert.True(last > before, "The fixture needs two chapters with text.");

        factory.Writer.Asked.Clear();
        var recap = await (await reader.PostAsJsonAsync($"/books/{book.Id}/recap", new RecapRequest(last, 0.5), JsonOptions)).Content.ReadFromJsonAsync<Recap>(JsonOptions);
        Assert.Equal(("The story so far.", last + 1, true), (recap!.Text, recap.Parts, recap.Partly));

        // Each earlier part is summed up from its own text; the recap sees the summaries and the latest passage read.
        var asked = factory.Writer.Asked.ToList();
        Assert.Contains(asked, ask => ask.Prompt.Contains(chapters[before][..100], StringComparison.Ordinal));
        var final = asked.Last().Prompt;
        Assert.Contains($"Part {before + 1}: Summary of a part.", final, StringComparison.Ordinal);
        var reached = chapters[last][..(chapters[last].Length / 2)];
        Assert.Contains(reached[^40..], final, StringComparison.Ordinal);
        // Nothing after the place goes anywhere.
        var after = chapters[last][(chapters[last].Length / 2 + 20)..];
        Assert.DoesNotContain(asked, ask => ask.Prompt.Contains(after[^40..], StringComparison.Ordinal));

        // Asked again, the summaries are kept: only the recap is written.
        var count = factory.Writer.Asked.Count;
        await reader.PostAsJsonAsync($"/books/{book.Id}/recap", new RecapRequest(last, 0.5), JsonOptions);
        Assert.Equal(count + 1, factory.Writer.Asked.Count);

        var stranger = await factory.SignUpAsync("Recap Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/books/{book.Id}/recap", new RecapRequest(0, 0.5), JsonOptions)).StatusCode);
    }

    [Fact]
    public async Task The_writer_asks_anthropics_messages_api_with_the_key()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var handler = new Answers(async request =>
        {
            (sent, body) = (request, await request.Content!.ReadAsStringAsync());
            return """{"content":[{"type":"text","text":"Ishmael went to sea."}]}""";
        });
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Recap:ApiKey"] = "sk-test" }).Build();
        var text = await new AnthropicWriter(new HttpClient(handler), configuration).WriteAsync("system", "prompt", 300, CancellationToken.None);
        Assert.Equal("Ishmael went to sea.", text);
        Assert.Equal(("https://api.anthropic.com/v1/messages", "sk-test", "2023-06-01"), (sent!.RequestUri!.AbsoluteUri, sent.Headers.GetValues("x-api-key").Single(), sent.Headers.GetValues("anthropic-version").Single()));
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(("claude-haiku-5-5", 300, "prompt"), (json.RootElement.GetProperty("model").GetString(), json.RootElement.GetProperty("max_tokens").GetInt32(), json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()));
    }

    private sealed class Answers(Func<HttpRequestMessage, Task<string>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await answer(request), System.Text.Encoding.UTF8, "application/json") };
    }
}

// A model that sums up each part the same way, and writes the same recap.
public sealed class StubRecapWriter : IRecapWriter
{
    public bool Ready => true;

    public ConcurrentQueue<(string System, string Prompt)> Asked { get; } = new();

    public Task<string?> WriteAsync(string system, string prompt, int most, CancellationToken cancellationToken)
    {
        Asked.Enqueue((system, prompt));
        return Task.FromResult<string?>(system.Contains("previously", StringComparison.Ordinal) ? "The story so far." : "Summary of a part.");
    }
}
