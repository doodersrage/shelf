using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class HardcoverTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Changed_books_go_to_hardcover_and_unchanged_ones_do_not()
    {
        var reader = await factory.SignUpAsync("Hardcover Reader");
        var readerId = await factory.ReaderIdAsync("Hardcover Reader");
        var finished = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Piranesi", "Susanna Clarke", BookStatus.Finished, 5, Isbn: "9781635575996", StartedOn: new DateOnly(2025, 1, 2), FinishedOn: new DateOnly(2025, 1, 9)), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Not On Hardcover", "Nobody", BookStatus.Want, null), JsonOptions);
        factory.Hardcover.Books["9781635575996"] = 501;
        var sync = factory.Services.GetRequiredService<HardcoverSync>();

        await using (var scope = Scope(readerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.Null(await sync.ConnectAsync(db, "wrong"));
            Assert.Equal("reader42", await sync.ConnectAsync(db, StubHardcover.Good));
            var sent = await sync.SendAsync(db);
            Assert.Equal((true, 1, 1), (sent.Done, sent.Books, sent.NotFound));
        }

        var saved = Assert.Single(factory.Hardcover.Saved);
        Assert.Equal((501, (int?)null, new HardcoverStatus(3, 5, new DateOnly(2025, 1, 2), new DateOnly(2025, 1, 9))), (saved.BookId, saved.UserBookId, saved.Status));

        // Nothing changed, nothing goes; a change goes to the same copy there.
        await using (var scope = Scope(readerId))
        {
            Assert.Equal((0, 0), await Counts(sync, scope));
        }

        await reader.PutAsJsonAsync($"/books/{finished.Id}", new UpdateBookRequest("Piranesi", "Susanna Clarke", BookStatus.Finished, 4, Isbn: "9781635575996", StartedOn: new DateOnly(2025, 1, 2), FinishedOn: new DateOnly(2025, 1, 9)), JsonOptions);
        await using (var scope = Scope(readerId))
        {
            Assert.Equal((1, 0), await Counts(sync, scope));
        }

        Assert.Equal((501, (int?)9000, 4), factory.Hardcover.Saved.Last() is var last ? (last.BookId, last.UserBookId, last.Status.Rating) : default);
    }

    [Fact]
    public async Task The_client_asks_hardcovers_graphql_with_the_token()
    {
        var asked = new ConcurrentQueue<(string Auth, string Body)>();
        var handler = new Answers(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            asked.Enqueue((request.Headers.Authorization!.ToString(), body));
            return body.Contains("me {", StringComparison.Ordinal) ? """{"data":{"me":[{"username":"reader42"}]}}"""
                : body.Contains("editions", StringComparison.Ordinal) ? """{"data":{"editions":[{"book_id":501}]}}"""
                : body.Contains("insert_user_book(", StringComparison.Ordinal) ? """{"data":{"insert_user_book":{"id":9000,"error":null}}}"""
                : """{"data":{"insert_user_book_read":{"id":1,"error":null}}}""";
        });
        var client = new HardcoverClient(new HttpClient(handler));

        Assert.Equal("reader42", await client.WhoAsync("Bearer secret", CancellationToken.None));
        Assert.Equal(501, await client.FindAsync("secret", "9781635575996", "Piranesi", "Susanna Clarke", CancellationToken.None));
        Assert.Equal(9000, await client.SaveAsync("secret", 501, null, new HardcoverStatus(3, 5, null, new DateOnly(2025, 1, 9)), CancellationToken.None));
        Assert.All(asked, item => Assert.Equal("Bearer secret", item.Auth));
        Assert.Contains(asked, item => item.Body.Contains("isbn_13", StringComparison.Ordinal));
        Assert.Contains(asked, item => item.Body.Contains("\"finished_at\":\"2025-01-09\"", StringComparison.Ordinal));
    }

    private static async Task<(int, int)> Counts(HardcoverSync sync, AsyncServiceScope scope)
    {
        var sent = await sync.SendAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>());
        return (sent.Books, sent.NotFound);
    }

    private AsyncServiceScope Scope(int readerId)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope;
    }

    private sealed class Answers(Func<HttpRequestMessage, Task<string>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await answer(request), Encoding.UTF8, "application/json") };
    }
}

public sealed class StubHardcover : IHardcoverClient
{
    public const string Good = "hardcover-token";

    public ConcurrentDictionary<string, int> Books { get; } = new();

    public ConcurrentQueue<(int BookId, int? UserBookId, HardcoverStatus Status)> Saved { get; } = new();

    public Task<string?> WhoAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == Good ? "reader42" : null);

    public Task<int?> FindAsync(string token, string? isbn, string title, string author, CancellationToken cancellationToken) =>
        Task.FromResult(isbn is not null && Books.TryGetValue(isbn, out var id) ? id : (int?)null);

    public Task<int?> SaveAsync(string token, int bookId, int? userBookId, HardcoverStatus status, CancellationToken cancellationToken)
    {
        Saved.Enqueue((bookId, userBookId, status));
        return Task.FromResult<int?>(userBookId ?? 9000);
    }
}
