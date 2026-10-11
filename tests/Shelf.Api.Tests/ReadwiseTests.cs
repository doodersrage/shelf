using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class ReadwiseTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Highlights_and_quotes_go_to_readwise_once_connected_and_new_ones_on_their_own()
    {
        var reader = await factory.SignUpAsync("Readwise Reader");
        var readerId = await factory.ReaderIdAsync("Readwise Reader");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Middlemarch", "George Eliot", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        await reader.PostAsJsonAsync($"/books/{book.Id}/quotes", new { text = "It is never too late to be what you might have been.", page = 12 }, JsonOptions);
        await reader.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("the growing good of the world", 3, "The ending."), JsonOptions);
        var sync = factory.Services.GetRequiredService<ReadwiseSync>();

        await using (var scope = Scope(readerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.False(await sync.ConnectAsync(db, "not-a-token"));
            Assert.True(await sync.ConnectAsync(db, StubReadwise.Good));
            // The token is kept sealed, never as it was given.
            var kept = await db.Readers.AsNoTracking().Where(item => item.Id == readerId).Select(item => item.ReadwiseToken).SingleAsync();
            Assert.NotEqual(StubReadwise.Good, kept);

            var sent = await sync.SendAsync(db, everything: true);
            Assert.Equal((true, 2), (sent.Done, sent.Count));
            var quote = factory.Readwise.Sent.Single(item => item.Text.StartsWith("It is never", StringComparison.Ordinal));
            Assert.Equal(("Middlemarch", "George Eliot", 12, "page"), (quote.Title, quote.Author, quote.Location, quote.LocationType));
            Assert.Equal(("The ending.", 4, "order"), factory.Readwise.Sent.Where(item => item.Text == "the growing good of the world").Select(item => (item.Note, item.Location, item.LocationType)).Single());

            var me = await db.Readers.FirstAsync(item => item.Id == readerId);
            me.ReadwiseAuto = true;
            await db.SaveChangesAsync();
        }

        // On their own, only those made since go.
        await reader.PostAsJsonAsync($"/books/{book.Id}/quotes", new { text = "A newer line.", page = 40 }, JsonOptions);
        var before = factory.Readwise.Sent.Count;
        Assert.Equal(1, await sync.SendDueAsync(CancellationToken.None));
        Assert.Equal(before + 1, factory.Readwise.Sent.Count);

        await using (var scope = Scope(readerId))
        {
            await ReadwiseSync.DisconnectAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>());
            Assert.False((await sync.SendAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), everything: true)).Done);
        }
    }

    [Fact]
    public async Task Highlights_made_elsewhere_come_from_readwise_onto_their_books_once()
    {
        var reader = await factory.SignUpAsync("Readwise Importer");
        var readerId = await factory.ReaderIdAsync("Readwise Importer");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Piranesi", "Susanna Clarke", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        factory.Readwise.Export.Clear();
        factory.Readwise.Export.Add(new ReadwiseBook("Piranesi", "Clarke, Susanna", "books", "kindle",
        [
            new ReadwiseExported("The Beauty of the House is immeasurable.", "Opening line", 7, "page", new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero), false),
            new ReadwiseExported("Thrown away.", null, null, null, null, true),
        ]));
        factory.Readwise.Export.Add(new ReadwiseBook("Not On This Shelf", "Nobody", "books", "kindle", [new ReadwiseExported("Lost.", null, null, null, null, false)]));
        factory.Readwise.Export.Add(new ReadwiseBook("Piranesi", "Susanna Clarke", "books", "shelf", [new ReadwiseExported("Sent from Shelf.", null, null, null, null, false)]));
        factory.Readwise.Export.Add(new ReadwiseBook("An Article", "Someone", "articles", "reader", [new ReadwiseExported("Not a book.", null, null, null, null, false)]));
        var sync = factory.Services.GetRequiredService<ReadwiseSync>();

        await using (var scope = Scope(readerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var store = scope.ServiceProvider.GetRequiredService<EbookStore>();
            Assert.False((await sync.ImportAsync(db, store, everything: true)).Done);
            await sync.ConnectAsync(db, StubReadwise.Good);
            var brought = (await sync.ImportAsync(db, store, everything: true)).Import!;
            Assert.Equal((1, 1), (brought.Quotes, brought.Books));
            Assert.Equal(["Not On This Shelf"], brought.NotFound);
            Assert.Null(factory.Readwise.ExportedAfter.Last());

            // Again: nothing twice, and only what changed since is asked for.
            Assert.Equal(1, (await sync.ImportAsync(db, store, everything: false)).Import!.AlreadyHere);
            Assert.NotNull(factory.Readwise.ExportedAfter.Last());
        }

        var quotes = await reader.GetFromJsonAsync<QuoteResponse[]>($"/books/{book.Id}/quotes", JsonOptions);
        Assert.Equal(("The Beauty of the House is immeasurable.", (int?)7), (Assert.Single(quotes!).Text, quotes![0].Page));
    }

    [Fact]
    public async Task The_client_reads_readwises_export_page_by_page()
    {
        var asked = new ConcurrentQueue<string>();
        var handler = new Answers(request =>
        {
            asked.Enqueue(request.RequestUri!.Query);
            return request.RequestUri.Query.Contains("pageCursor", StringComparison.Ordinal)
                ? """{"count":2,"nextPageCursor":null,"results":[{"title":"Second","author":"B","category":"books","source":"kindle","highlights":[]}]}"""
                : """{"count":2,"nextPageCursor":"abc","results":[{"readable_title":"First","title":"first","author":"A","category":"books","source":"kindle","highlights":[{"text":"Words","note":"","location":12,"location_type":"page","highlighted_at":"2025-03-01T10:00:00Z","is_discard":false}]}]}""";
        });
        var books = await new ReadwiseClient(new HttpClient(handler)).ExportAsync("token", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal(["First", "Second"], books!.Select(book => book.Title));
        Assert.Equal(new ReadwiseExported("Words", null, 12, "page", new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.Zero), false), books[0].Highlights.Single());
        Assert.Contains("updatedAfter=2026-01-01T00%3A00%3A00.0000000Z", asked.First(), StringComparison.Ordinal);
        Assert.Contains("pageCursor=abc", asked.Last(), StringComparison.Ordinal);
    }

    private sealed class Answers(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(answer(request), System.Text.Encoding.UTF8, "application/json") });
    }

    private AsyncServiceScope Scope(int readerId)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope;
    }
}

public sealed class StubReadwise : IReadwiseClient
{
    public const string Good = "readwise-token-that-works";

    public ConcurrentQueue<ReadwiseHighlight> Sent { get; } = new();

    public List<ReadwiseBook> Export { get; } = [];

    public ConcurrentQueue<DateTimeOffset?> ExportedAfter { get; } = new();

    public Task<bool> CheckAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == Good);

    public Task<IReadOnlyList<ReadwiseBook>?> ExportAsync(string token, DateTimeOffset? updatedAfter, CancellationToken cancellationToken)
    {
        ExportedAfter.Enqueue(updatedAfter);
        return Task.FromResult<IReadOnlyList<ReadwiseBook>?>(token == Good ? [.. Export] : null);
    }

    public Task<bool> SendAsync(string token, IReadOnlyList<ReadwiseHighlight> highlights, CancellationToken cancellationToken)
    {
        if (token != Good)
        {
            return Task.FromResult(false);
        }

        foreach (var highlight in highlights)
        {
            Sent.Enqueue(highlight);
        }

        return Task.FromResult(true);
    }
}
