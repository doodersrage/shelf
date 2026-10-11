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

public sealed class FindingTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task A_followed_authors_new_books_are_wanted_straight_away_when_asked_and_only_for_the_follower()
    {
        var follower = await factory.SignUpAsync("Ged Follower");
        var other = await factory.SignUpAsync("Vetch Other");
        var followerId = await factory.ReaderIdAsync("Ged Follower");
        factory.BookSearch.ByAuthor["Ursula Writer"] =
        [
            new CatalogHit("/works/A1", "The Newest One", "Ursula Writer", 2099, "9780000000026", null, 220),
            new CatalogHit("/works/A2", "The Newest One", "Ursula Writer", 2099, null, null, null),
            new CatalogHit("/works/A3", "An Old One", "Ursula Writer", 1971, null, null, null),
            new CatalogHit("/works/A4", "Not Hers", "Someone Else", 2099, null, null, null),
        ];

        int followId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(followerId);
            await Finding.FollowAsync(db, "Ursula Writer");
            // The same author, written another way, is followed once.
            await Finding.FollowAsync(db, "Writer, Ursula");
            var follows = await Finding.FollowsAsync(db);
            followId = Assert.Single(follows).Id;
            await Finding.SetAutoFetchAsync(db, followId, true);
        }

        var watch = factory.Services.GetRequiredService<AuthorWatch>();
        Assert.Equal(1, await watch.CheckAsync(followId, CancellationToken.None));

        // On the want list with what the catalog knew; nothing left waiting to be offered, as it was wanted already.
        var wanted = await follower.GetFromJsonAsync<BookResponse[]>("/books?status=Want", JsonOptions);
        var book = Assert.Single(wanted!, item => item.Title == "The Newest One");
        Assert.Equal("9780000000026", book.Isbn);
        Assert.Equal(220, book.Pages);
        Assert.DoesNotContain(wanted!, item => item.Title is "An Old One" or "Not Hers");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(followerId);
            Assert.Empty(await Finding.AlertsAsync(db));
        }

        // Looked for again: nothing new. And nothing reached the other reader's shelf.
        Assert.Equal(0, await watch.CheckAsync(followId, CancellationToken.None));
        Assert.DoesNotContain((await other.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!, item => item.Title == "The Newest One");
    }
}

public sealed class StubBookSearch : IBookSearch
{
    public ConcurrentDictionary<string, IReadOnlyList<CatalogHit>> ByAuthor { get; } = new();

    public Task<IReadOnlyList<CatalogHit>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CatalogHit>>([]);

    public Task<IReadOnlyList<CatalogHit>> ByAuthorAsync(string author, CancellationToken cancellationToken) =>
        Task.FromResult(ByAuthor.TryGetValue(author, out var hits) ? hits : []);
}
