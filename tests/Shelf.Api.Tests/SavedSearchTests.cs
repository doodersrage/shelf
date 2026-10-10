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
}
