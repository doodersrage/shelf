using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
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

    // A token Hardcover turns down is told on Connections, and no book is taken for one not on Hardcover because it
    // couldn't be looked up; a send that goes right clears it.
    [Fact]
    public async Task Hardcover_trouble_is_told_and_books_are_not_given_up_on()
    {
        var reader = await factory.SignUpAsync("Hardcover Revoked");
        var readerId = await factory.ReaderIdAsync("Hardcover Revoked");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Waiting To Go", "Someone", BookStatus.Reading, null, Isbn: "9780000000040"), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        factory.Hardcover.Books["9780000000040"] = 640;
        var sync = factory.Services.GetRequiredService<HardcoverSync>();
        await using (var scope = Scope(readerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var me = await db.Readers.FirstAsync(item => item.Id == readerId);
            me.HardcoverToken = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Shelf.Hardcover.Token").Protect("a-revoked-token");
            me.HardcoverAuto = true;
            await db.SaveChangesAsync();
        }

        await sync.SendDueAsync(CancellationToken.None);
        await using (var scope = Scope(readerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var hardcover = (await Connections.ForReaderAsync(db)).Single(item => item.Name == "Hardcover");
            Assert.Equal(ConnectionHealth.Trouble, hardcover.Health);
            Assert.Contains("turned the token down", hardcover.Note);
            Assert.Null((await db.Books.AsNoTracking().FirstAsync(item => item.Id == book.Id)).HardcoverState);

            await sync.ConnectAsync(db, StubHardcover.Good);
            Assert.Equal(1, (await sync.SendAsync(db)).Books);
            Assert.Equal(ConnectionHealth.Working, (await Connections.ForReaderAsync(db)).Single(item => item.Name == "Hardcover").Health);
        }
    }

    [Fact]
    public async Task Changes_on_hardcover_come_back_unless_the_book_changed_here_too()
    {
        var reader = await factory.SignUpAsync("Hardcover Puller");
        var readerId = await factory.ReaderIdAsync("Hardcover Puller");
        var linked = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Linked", "Author One", BookStatus.Reading, null, Isbn: "9780000000019"), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        var both = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Both Changed", "Author Two", BookStatus.Reading, null, Isbn: "9780000000026"), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        var fresh = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Never Sent", "Author Three", BookStatus.Want, 5), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        factory.Hardcover.Books["9780000000019"] = 601;
        factory.Hardcover.Books["9780000000026"] = 602;
        var sync = factory.Services.GetRequiredService<HardcoverSync>();
        await using (var scope = Scope(readerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            await sync.ConnectAsync(db, StubHardcover.Good);
            Assert.Equal(2, (await sync.SendAsync(db)).Books);
            var me = await db.Readers.FirstAsync(item => item.Id == readerId);
            me.HardcoverAddMissing = true;
            await db.SaveChangesAsync();
        }

        // Here, one book changes; on Hardcover, it and another do, a third is matched by title, and a fourth is new.
        await reader.PutAsJsonAsync($"/books/{both.Id}", new UpdateBookRequest("Both Changed", "Author Two", BookStatus.Abandoned, null, Isbn: "9780000000026"), JsonOptions);
        factory.Hardcover.Shelf.Clear();
        factory.Hardcover.Shelf.AddRange(
        [
            new HardcoverShelfBook(9100, 601, "Linked", "Author One", "9780000000019", new HardcoverStatus(3, 4, null, new DateOnly(2026, 9, 1))),
            new HardcoverShelfBook(9101, 602, "Both Changed", "Author Two", "9780000000026", new HardcoverStatus(3, 2, null, null)),
            new HardcoverShelfBook(9300, 700, "Never sent", "Author Three", null, new HardcoverStatus(2, 3, new DateOnly(2026, 8, 1), null)),
            new HardcoverShelfBook(9200, 701, "Only There", "Author Four", "9780000000033", new HardcoverStatus(1, null, null, null)),
        ]);
        await using (var scope = Scope(readerId))
        {
            var brought = await sync.PullAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>());
            Assert.Equal((true, 2, 1), (brought.Done, brought.Changed, brought.Added));
        }

        var afterLinked = await reader.GetFromJsonAsync<BookResponse>($"/books/{linked.Id}", JsonOptions);
        Assert.Equal((BookStatus.Finished, (int?)4, (DateOnly?)new DateOnly(2026, 9, 1)), (afterLinked!.Status, afterLinked.Rating, afterLinked.FinishedOn));
        Assert.Equal(BookStatus.Abandoned, (await reader.GetFromJsonAsync<BookResponse>($"/books/{both.Id}", JsonOptions))!.Status);
        var afterFresh = await reader.GetFromJsonAsync<BookResponse>($"/books/{fresh.Id}", JsonOptions);
        Assert.Equal((BookStatus.Reading, (int?)5, (DateOnly?)new DateOnly(2026, 8, 1)), (afterFresh!.Status, afterFresh.Rating, afterFresh.StartedOn));
        var all = await reader.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions);
        Assert.Contains(all!, book => book.Title == "Only There" && book.Status == BookStatus.Want);

        // What came back is not sent back. What changed only here goes, as do the never-sent book's own stars, and the
        // linked book's start date, which Shelf set when it was added as Reading and Hardcover does not have.
        await using (var scope = Scope(readerId))
        {
            var before = factory.Hardcover.Saved.Count;
            var sent = await sync.SendAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>());
            var went = factory.Hardcover.Saved.Skip(before).ToList();
            Assert.Equal([601, 602, 700], went.Select(item => item.BookId).Order());
            Assert.Equal(5, went.Single(item => item.BookId == 700).Status.Rating);
            Assert.Equal(3, sent.Books);
        }
    }

    [Fact]
    public async Task The_client_reads_the_accounts_shelf_from_hardcover()
    {
        var handler = new Answers(_ => Task.FromResult("""
            {"data":{"me":[{"user_books":[
              {"id":9001,"book_id":501,"status_id":3,"rating":4.5,"book":{"title":"Piranesi","contributions":[{"author":{"name":"Susanna Clarke"}}]},
               "edition":{"isbn_13":"9781635575996","isbn_10":null},"user_book_reads":[{"started_at":"2025-01-02","finished_at":"2025-01-09"}]},
              {"id":9002,"book_id":502,"status_id":1,"rating":null,"book":{"title":"Jonathan Strange","contributions":[]},"edition":null,"user_book_reads":[]}
            ]}]}}
            """));
        var shelf = await new HardcoverClient(new HttpClient(handler)).ShelfAsync("secret", CancellationToken.None);
        Assert.Equal(
            [
                new HardcoverShelfBook(9001, 501, "Piranesi", "Susanna Clarke", "9781635575996", new HardcoverStatus(3, 5, new DateOnly(2025, 1, 2), new DateOnly(2025, 1, 9))),
                new HardcoverShelfBook(9002, 502, "Jonathan Strange", "", null, new HardcoverStatus(1, null, null, null)),
            ],
            shelf);
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

    public List<HardcoverShelfBook> Shelf { get; } = [];

    public ConcurrentDictionary<int, CatalogMatch> Details { get; } = new();

    public Task<IReadOnlyList<HardcoverShelfBook>?> ShelfAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HardcoverShelfBook>?>(token == Good ? [.. Shelf] : null);

    public Task<string?> WhoAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == Good ? "reader42" : null);

    public Task<SyncProblem?> DiagnoseAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult<SyncProblem?>(token == Good ? null : SyncProblem.Refused);

    public Task<int?> FindAsync(string token, string? isbn, string title, string author, CancellationToken cancellationToken) =>
        Task.FromResult(isbn is not null && Books.TryGetValue(isbn, out var id) ? id : (int?)null);

    public Task<CatalogMatch?> DetailsAsync(string token, int bookId, CancellationToken cancellationToken) =>
        Task.FromResult(token == Good && Details.TryGetValue(bookId, out var match) ? match : null);

    public Task<int?> SaveAsync(string token, int bookId, int? userBookId, HardcoverStatus status, CancellationToken cancellationToken)
    {
        Saved.Enqueue((bookId, userBookId, status));
        // A new copy there gets an id of its own: 9000 for book 501.
        return Task.FromResult<int?>(userBookId ?? 8499 + bookId);
    }
}
