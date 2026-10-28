using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
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
        Assert.NotNull(books);
        Assert.Equal(["First", "Second"], books.Select(book => book.Title));
        Assert.Equal(new ReadwiseExported("Words", null, 12, "page", new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.Zero), false), books[0].Highlights.Single());
        Assert.Contains("updatedAfter=2026-01-01T00%3A00%3A00.0000000Z", asked.First(), StringComparison.Ordinal);
        Assert.Contains("pageCursor=abc", asked.Last(), StringComparison.Ordinal);
    }

    private sealed class Answers(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(answer(request), System.Text.Encoding.UTF8, "application/json") });
    }

    // One reader's Readwise failing in a way nothing expected doesn't stop the hourly round for the readers after them.
    [Fact]
    public async Task One_reader_s_readwise_failing_leaves_the_others_to_go()
    {
        var broken = await factory.SignUpAsync("Readwise Broken");
        var brokenId = await factory.ReaderIdAsync("Readwise Broken");
        var fine = await factory.SignUpAsync("Readwise After");
        var fineId = await factory.ReaderIdAsync("Readwise After");
        var sync = factory.Services.GetRequiredService<ReadwiseSync>();
        foreach (var (client, text) in new[] { (broken, "A line that never arrives."), (fine, "A line that still goes.") })
        {
            var book = (await (await client.PostAsJsonAsync("/books", new CreateBookRequest("Persuasion", "Jane Austen", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
            await client.PostAsJsonAsync($"/books/{book.Id}/quotes", new { text, page = 3 }, JsonOptions);
        }

        await using (var scope = Scope(brokenId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var me = await db.Readers.FirstAsync(item => item.Id == brokenId);
            me.ReadwiseToken = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Shelf.Readwise.Token").Protect(StubReadwise.Broken);
            me.ReadwiseAuto = true;
            await db.SaveChangesAsync();
        }

        await using (var scope = Scope(fineId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.True(await sync.ConnectAsync(db, StubReadwise.Good));
            var me = await db.Readers.FirstAsync(item => item.Id == fineId);
            me.ReadwiseAuto = true;
            await db.SaveChangesAsync();
        }

        Assert.True(brokenId < fineId);
        await sync.SendDueAsync(CancellationToken.None);
        Assert.Contains(factory.Readwise.Sent, item => item.Text == "A line that still goes.");
        Assert.DoesNotContain(factory.Readwise.Sent, item => item.Text == "A line that never arrives.");
    }

    // A token Readwise turns down, or one the shelf can no longer read, is told on Connections and Account with what
    // to do, where before the hourly send failed quietly; it goes once a trade goes right.
    [Fact]
    public async Task Readwise_trouble_is_told_to_the_reader_until_a_trade_goes_right()
    {
        var reader = await factory.SignUpAsync("Readwise Revoked");
        var readerId = await factory.ReaderIdAsync("Readwise Revoked");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Emma", "Jane Austen", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        await reader.PostAsJsonAsync($"/books/{book.Id}/quotes", new { text = "A line kept for Readwise.", page = 1 }, JsonOptions);
        var sync = factory.Services.GetRequiredService<ReadwiseSync>();
        async Task SealAsync(string sealedToken)
        {
            await using var scope = Scope(readerId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var me = await db.Readers.FirstAsync(item => item.Id == readerId);
            (me.ReadwiseToken, me.ReadwiseAuto) = (sealedToken, true);
            await db.SaveChangesAsync();
        }

        async Task<ConnectionStatus> ReadwiseAsync()
        {
            await using var scope = Scope(readerId);
            return (await Connections.ForReaderAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>())).Single(item => item.Name == "Readwise");
        }

        // Turned down: said so, here and on Account.
        await using (var scope = Scope(readerId))
        {
            await SealAsync(scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Shelf.Readwise.Token").Protect("a-revoked-token"));
        }

        await sync.SendDueAsync(CancellationToken.None);
        var trouble = await ReadwiseAsync();
        Assert.Equal(ConnectionHealth.Trouble, trouble.Health);
        Assert.Contains("turned the token down", trouble.Note);
        Assert.Contains("turned the token down", System.Net.WebUtility.HtmlDecode(await reader.GetStringAsync("/account?tab=tab-connections")));

        // A token the shelf's keys can no longer open says that instead.
        await SealAsync("not sealed with this shelf's keys");
        await sync.SendDueAsync(CancellationToken.None);
        Assert.Contains("can't be read", (await ReadwiseAsync()).Note);

        // Connected again, a trade that goes right clears it.
        await using (var scope = Scope(readerId))
        {
            Assert.True(await sync.ConnectAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), StubReadwise.Good));
        }

        await sync.SendDueAsync(CancellationToken.None);
        Assert.Equal(ConnectionHealth.Working, (await ReadwiseAsync()).Health);
    }

    // Bringing highlights going wrong is still told when sending them goes right in the same round.
    [Fact]
    public async Task Trouble_bringing_highlights_is_kept_when_sending_goes_right()
    {
        var reader = await factory.SignUpAsync("Readwise Half Way");
        var readerId = await factory.ReaderIdAsync("Readwise Half Way");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Mansfield Park", "Jane Austen", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        await reader.PostAsJsonAsync($"/books/{book.Id}/quotes", new { text = "A line that goes out.", page = 2 }, JsonOptions);
        await using (var scope = Scope(readerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var me = await db.Readers.FirstAsync(item => item.Id == readerId);
            me.ReadwiseToken = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Shelf.Readwise.Token").Protect(StubReadwise.SendsOnly);
            (me.ReadwiseAuto, me.ReadwiseAutoImport) = (true, true);
            await db.SaveChangesAsync();
        }

        await factory.Services.GetRequiredService<ReadwiseSync>().SendDueAsync(CancellationToken.None);
        Assert.Contains(factory.Readwise.Sent, item => item.Text == "A line that goes out.");
        await using (var scope = Scope(readerId))
        {
            var readwise = (await Connections.ForReaderAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>())).Single(item => item.Name == "Readwise");
            Assert.Equal(ConnectionHealth.Trouble, readwise.Health);
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

    // A token whose answers come back in a shape nothing expected.
    public const string Broken = "readwise-token-whose-answers-make-no-sense";

    // A token highlights go out with, but nothing comes back by.
    public const string SendsOnly = "readwise-token-that-only-sends";

    public ConcurrentQueue<ReadwiseHighlight> Sent { get; } = new();

    public List<ReadwiseBook> Export { get; } = [];

    public ConcurrentQueue<DateTimeOffset?> ExportedAfter { get; } = new();

    public Task<bool> CheckAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == Good);

    // A token Readwise takes answers its check whatever its answers say; any other is turned down.
    public Task<SyncProblem?> DiagnoseAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult<SyncProblem?>(token is Good or Broken or SendsOnly ? null : SyncProblem.Refused);

    public Task<IReadOnlyList<ReadwiseBook>?> ExportAsync(string token, DateTimeOffset? updatedAfter, CancellationToken cancellationToken)
    {
        ExportedAfter.Enqueue(updatedAfter);
        return Task.FromResult<IReadOnlyList<ReadwiseBook>?>(token == Good ? [.. Export] : null);
    }

    public Task<bool> SendAsync(string token, IReadOnlyList<ReadwiseHighlight> highlights, CancellationToken cancellationToken)
    {
        if (token == Broken)
        {
            throw new JsonException("Not the answer Readwise gives.");
        }

        if (token is not (Good or SendsOnly))
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
