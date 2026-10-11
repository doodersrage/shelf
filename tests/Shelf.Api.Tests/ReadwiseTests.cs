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

    public Task<bool> CheckAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == Good);

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
