using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class FederationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures"));

    [Fact]
    public async Task Readers_on_linked_shelves_borrow_from_each_other_and_the_copy_goes_back()
    {
        var router = new Router();
        await using var home = new ShelfApiFactory { PublicAddress = "https://home.test", LinkHandler = router };
        await using var away = new ShelfApiFactory { PublicAddress = "https://away.test", LinkHandler = router };
        router.Shelves["home.test"] = home;
        router.Shelves["away.test"] = away;
        await home.InitializeAsync();
        await away.InitializeAsync();

        // The admins link the two shelves with a one-time code.
        string code;
        await using (var scope = Scope(home, home.ReaderId))
        {
            code = (await scope.ServiceProvider.GetRequiredService<Federation>().InviteAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), "https://home.test")).Code!;
        }

        await using (var scope = Scope(away, away.ReaderId))
        {
            var federation = scope.ServiceProvider.GetRequiredService<Federation>();
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var (link, problem) = await federation.JoinAsync(db, code, "https://away.test");
            Assert.Null(problem);
            Assert.Equal("https://home.test", link!.Address);
            Assert.NotNull((await federation.JoinAsync(db, code, "https://away.test")).Problem);
        }

        await using (var scope = Scope(home, home.ReaderId))
        {
            Assert.Equal(("https://away.test", true), Assert.Single(await Federation.ListAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>())) is var link ? (link.Address, link.Active) : default);
        }

        // At home, a reader opens their shelf; it has an EPUB.
        var owner = home.Client;
        var book = (await (await owner.PostAsJsonAsync("/books", new CreateBookRequest("Moby-Dick", "Herman Melville", BookStatus.Finished, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(File.OpenRead(Path.Combine(Fixtures, "moby.epub"))), "file", "moby.epub" } })
        {
            (await owner.PostAsync($"/books/{book.Id}/ebook", content)).EnsureSuccessStatusCode();
        }

        await owner.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(true), JsonOptions);

        // Away, a reader sees it and asks to borrow it.
        var borrowerId = away.ReaderId;
        await using (var scope = Scope(away, borrowerId))
        {
            var federation = scope.ServiceProvider.GetRequiredService<Federation>();
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var shelf = Assert.Single(await federation.ShelvesAsync(db));
            var books = (await federation.ShelfAsync(db, shelf.LinkId, shelf.OwnerId))!.Value.Books;
            Assert.Equal(("Moby-Dick", true, false), (Assert.Single(books).Title, books[0].HasEbook, books[0].Asked));
            Assert.True(await federation.AskAsync(db, shelf.LinkId, shelf.OwnerId, book.Id));
            Assert.Single(await Federation.LinkedAsksAsync(db));
        }

        // At home, the owner sees the ask, and lends it.
        await using (var scope = Scope(home, home.ReaderId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var ask = Assert.Single(await Federation.RemoteAsksAsync(db));
            Assert.Equal(("Tenar", "Moby-Dick"), (ask.Reader, ask.Title));
            Assert.Equal(LendResult.Lent, await Federation.LendRemoteAskAsync(db, ask.Id, new DateOnly(2030, 1, 1)));
        }

        Assert.StartsWith("Tenar (", (await owner.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!.LoanedTo, StringComparison.Ordinal);

        // Away, the copy arrives with its file, readable there, and goes when it is given back.
        int copyId;
        await using (var scope = Scope(away, borrowerId))
        {
            var federation = scope.ServiceProvider.GetRequiredService<Federation>();
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            await federation.SyncAsync(db);
            var borrowed = Assert.Single(await Lending.BorrowedAsync(db));
            Assert.Equal(("Moby-Dick", true, true, (DateOnly?)new DateOnly(2030, 1, 1)), (borrowed.Title, borrowed.Linked, borrowed.HasEbook, borrowed.DueOn));
            Assert.Empty(await Federation.LinkedAsksAsync(db));
            copyId = borrowed.Id;
        }

        Assert.Equal(HttpStatusCode.OK, (await away.Client.GetAsync($"/books/{copyId}/ebook/chapters/0")).StatusCode);
        // A copy is not the borrower's to lend on.
        Assert.Equal(HttpStatusCode.BadRequest, (await away.Client.PostAsJsonAsync($"/books/{copyId}/lend", new LendRequest(away.ReaderId + 1000), JsonOptions)).StatusCode);

        await using (var scope = Scope(away, borrowerId))
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<Federation>().GiveBackAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), copyId));
        }

        var after = await away.Client.GetFromJsonAsync<BookResponse>($"/books/{copyId}", JsonOptions);
        Assert.Equal(("Moby-Dick", (string?)null), (after!.Title, after.EbookFileName));
        Assert.Null((await owner.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!.LoanedTo);

        // Without the link's secret, nothing is told.
        var stranger = home.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/federation/shelves")).StatusCode);
        stranger.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "link_guess");
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/federation/shelves")).StatusCode);
    }

    private static AsyncServiceScope Scope(ShelfApiFactory factory, int readerId)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope;
    }

    // Calls to a linked shelf go to that shelf's test server, by its host.
    private sealed class Router : HttpMessageHandler
    {
        public Dictionary<string, ShelfApiFactory> Shelves { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var shelf = Shelves[request.RequestUri!.Host];
            using var invoker = new HttpMessageInvoker(shelf.Server.CreateHandler(), disposeHandler: false);
            return await invoker.SendAsync(request, cancellationToken);
        }
    }
}
