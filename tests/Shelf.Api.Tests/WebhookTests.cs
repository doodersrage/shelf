using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class WebhookTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_readers_webhook_hears_of_their_own_books_signed_with_its_secret()
    {
        var reader = await factory.SignUpAsync("Hook Reader");
        var other = await factory.SignUpAsync("Hook Other");
        var readerId = await factory.ReaderIdAsync("Hook Reader");
        var webhooks = factory.Services.GetRequiredService<Webhooks>();
        NewWebhook made;
        await using (var scope = Scope(readerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.NotNull((await webhooks.AddAsync(db, "ftp://example.com/hook", WebhookFormat.Json, EventKinds.All)).Problem);
            made = (await webhooks.AddAsync(db, "https://hooks.example.com/reader", WebhookFormat.Json, [EventKinds.BookAdded, EventKinds.BookFinished])).Made!;
            await webhooks.AddAsync(db, "https://discord.example.com/api/webhooks/1", WebhookFormat.Discord, [EventKinds.BookFinished]);
        }

        Assert.StartsWith("whsec_", made.Secret, StringComparison.Ordinal);
        Assert.Equal([EventKinds.BookAdded, EventKinds.BookFinished], made.Webhook.Events);

        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Hooked", "Someone", BookStatus.Reading, null, Isbn: "9780000000002"), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        await other.PostAsJsonAsync("/books", new CreateBookRequest("Not Theirs", "Someone Else", BookStatus.Reading, null), JsonOptions);
        var added = await factory.Posts.WaitAsync(post => post.Url == "https://hooks.example.com/reader" && post.Body.Contains("Hooked", StringComparison.Ordinal));
        Assert.Equal("book.added", added.Headers["X-Shelf-Event"]);
        Assert.Equal("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(made.Secret), Encoding.UTF8.GetBytes(added.Body))), added.Headers["X-Shelf-Signature"]);
        using (var json = JsonDocument.Parse(added.Body))
        {
            Assert.Equal(("book.added", "Hooked", "reading", "Hook Reader"), (json.RootElement.GetProperty("event").GetString(), json.RootElement.GetProperty("book").GetProperty("title").GetString(),
                json.RootElement.GetProperty("book").GetProperty("status").GetString(), json.RootElement.GetProperty("actor").GetProperty("name").GetString()));
        }

        // Finishing it goes to both, the Discord one as a sentence; nobody else's book reaches either.
        await reader.PutAsJsonAsync($"/books/{book.Id}", new UpdateBookRequest("Hooked", "Someone", BookStatus.Finished, null, Isbn: "9780000000002"), JsonOptions);
        var discord = await factory.Posts.WaitAsync(post => post.Url.StartsWith("https://discord.example.com", StringComparison.Ordinal));
        Assert.Equal("Hook Reader finished Hooked (Someone).", JsonDocument.Parse(discord.Body).RootElement.GetProperty("content").GetString());
        await WaitIdleAsync();
        Assert.DoesNotContain(factory.Posts.Posted, post => post.Body.Contains("Not Theirs", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Posts.Posted, post => post.Url == "https://hooks.example.com/reader" && post.Headers["X-Shelf-Event"] == "book.started");
    }

    [Fact]
    public async Task A_webhook_that_keeps_failing_is_turned_off()
    {
        await factory.SignUpAsync("Failing Hook");
        var readerId = await factory.ReaderIdAsync("Failing Hook");
        var webhooks = factory.Services.GetRequiredService<Webhooks>();
        await using var scope = Scope(readerId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var made = (await webhooks.AddAsync(db, "https://down.example.com/hook", WebhookFormat.Slack, EventKinds.All)).Made!;
        for (var i = 0; i < Webhooks.MaxFailures; i++)
        {
            Assert.Equal(503, await webhooks.TestAsync(db, made.Webhook.Id));
        }

        var hook = Assert.Single(await Webhooks.ListAsync(db));
        Assert.Equal((false, Webhooks.MaxFailures, (int?)503), (hook.Enabled, hook.Failures, hook.LastStatus));
        Assert.Contains("A test from Shelf.", factory.Posts.Posted.Last().Body, StringComparison.Ordinal);
        await Webhooks.SetAsync(db, hook.Id, true);
        Assert.Equal((true, 0), (Assert.Single(await Webhooks.ListAsync(db)).Enabled, Assert.Single(await Webhooks.ListAsync(db)).Failures));
    }

    private async Task WaitIdleAsync()
    {
        var events = factory.Services.GetRequiredService<ShelfEvents>();
        for (var i = 0; i < 100 && !events.Idle; i++)
        {
            await Task.Delay(50);
        }
    }

    private AsyncServiceScope Scope(int readerId)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope;
    }
}

public sealed class StubWebhookPoster : IWebhookPoster
{
    public sealed record Post(string Url, string Body, IReadOnlyDictionary<string, string> Headers);

    public ConcurrentQueue<Post> Posted { get; } = new();

    // Addresses at down.example.com answer 503; the rest take it.
    public Task<int?> PostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        Posted.Enqueue(new Post(url, body, headers));
        return Task.FromResult<int?>(url.Contains("down.example.com", StringComparison.Ordinal) ? 503 : 200);
    }

    public async Task<Post> WaitAsync(Func<Post, bool> match)
    {
        for (var i = 0; i < 100; i++)
        {
            if (Posted.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("No such webhook was posted.");
    }
}
