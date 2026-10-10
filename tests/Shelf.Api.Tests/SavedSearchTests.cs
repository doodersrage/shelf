using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class SavedSearchTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    [Fact]
    public async Task A_readers_saved_searches_are_theirs_alone()
    {
        await factory.SignUpAsync("Saving Reader");
        await factory.SignUpAsync("Other Reader");
        var saver = await factory.ReaderIdAsync("Saving Reader");
        var other = await factory.ReaderIdAsync("Other Reader");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(saver);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            db.SavedSearches.Add(new SavedSearch { OwnerId = saver, Name = "Mine", Query = "status=Want&loved=1", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            Assert.Equal(["Mine"], await db.SavedSearches.Select(search => search.Name).ToListAsync());
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(other);
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<ShelfDb>().SavedSearches.ToListAsync());
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_shared_search_shows_its_books_to_anyone_with_the_link_and_nothing_private()
    {
        var reader = await factory.SignUpAsync("Sharing Reader");
        var readerId = await factory.ReaderIdAsync("Sharing Reader");
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("The Tombs of Atuan", "Ursula K. Le Guin", BookStatus.Finished, 5, Notes: "A private note", Loved: true, Pages: 180), JsonOptions);
        var other = await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Not Loved Enough", "Someone", BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions);

        string token;
        int savedId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var saved = new SavedSearch { OwnerId = readerId, Name = "My favourites", Query = "loved=1&sort=title", CreatedAt = DateTimeOffset.UtcNow };
            db.SavedSearches.Add(saved);
            await db.SaveChangesAsync();
            savedId = saved.Id;
            token = await SharedLists.ShareAsync(db, saved.Id);
        }

        var anyone = factory.CreateClient();
        var page = await anyone.GetStringAsync($"/shared/{token}");
        Assert.Contains("My favourites", page);
        Assert.Contains("The Tombs of Atuan", page);
        Assert.Contains("Sharing Reader", page);
        Assert.DoesNotContain("Not Loved Enough", page);
        Assert.DoesNotContain("A private note", page);
        Assert.Contains("noindex", page);

        var list = await anyone.GetFromJsonAsync<SharedList>($"/shared/{token}/list.json", JsonOptions);
        var book = Assert.Single(list!.Books);
        Assert.Equal(("The Tombs of Atuan", 5), (book.Title, book.Rating));
        Assert.DoesNotContain("private", await anyone.GetStringAsync($"/shared/{token}/list.json"), StringComparison.OrdinalIgnoreCase);

        // A cover comes only for a book on the list, and a made-up link opens nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"/shared/{token}/covers/{other!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync("/shared/not-a-real-link/list.json")).StatusCode);
        Assert.Contains("This list is not shared", await anyone.GetStringAsync("/shared/not-a-real-link"));

        // Stopping ends the link.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            await SharedLists.StopAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), savedId);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"/shared/{token}/list.json")).StatusCode);
    }
}
