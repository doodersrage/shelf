using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

// The everyday pages saying what matters now: Home's news and its sections, and how each connection stands.
public sealed class EverydayTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void Home_sections_keep_the_readers_order_and_add_any_new_one_shown()
    {
        Assert.Equal(BookRules.HomeSectionNames, BookRules.HomeSections(null).Select(section => section.Name));
        Assert.All(BookRules.HomeSections(null), section => Assert.True(section.Shown));

        var sections = BookRules.HomeSections("finished,-glance,nonsense,finished,next");
        Assert.Equal(["finished", "glance", "next", "reading", "added"], sections.Select(section => section.Name));
        Assert.Equal([true, false, true, true, true], sections.Select(section => section.Shown));
    }

    [Fact]
    public async Task Home_hides_a_section_and_brings_news_of_a_followed_series()
    {
        var client = await factory.SignUpAsync("Everyday Reader");
        var readerId = await factory.ReaderIdAsync("Everyday Reader");
        await client.PostAsJsonAsync("/books", new CreateBookRequest("Network Effect", "Martha Wells", BookStatus.Finished, null, Series: "The Murderbot Diaries", SeriesNumber: 5), JsonOptions);
        var home = await client.GetStringAsync("/");
        Assert.Contains("<h2>Recently added</h2>", home);
        Assert.DoesNotContain("Needs you", home);

        // Only from Home's own form, with its token.
        var forged = await client.PostAsync("/books/home", new FormUrlEncodedContent(new Dictionary<string, string> { ["shown"] = "glance" }));
        Assert.False(forged.IsSuccessStatusCode || (int)forged.StatusCode is >= 300 and < 400, forged.StatusCode.ToString());

        // Home's own form: only the glance ticked, and Recently finished moved up a place.
        var arranged = await ShelfApiFactory.PostFormAsync(client, "/", "/books/home", new() { ["shown"] = "glance", ["move"] = "finished:up" });
        Assert.True(arranged.IsSuccessStatusCode || (int)arranged.StatusCode is >= 300 and < 400, arranged.StatusCode.ToString());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.Equal(
                [("glance", true), ("reading", false), ("next", false), ("finished", false), ("added", false)],
                await BookRules.GetHomeSectionsAsync(db));
            db.SeriesAlerts.Add(new SeriesAlert { OwnerId = readerId, Series = "The Murderbot Diaries", Author = "Martha Wells", Title = "System Collapse", WorkKey = "/works/OL1W", FoundAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        home = await client.GetStringAsync("/");
        Assert.DoesNotContain("<h2>Recently added</h2>", home);
        Assert.Contains("Needs you", home);
        Assert.Contains("New in The Murderbot Diaries: System Collapse", home);

        // Another reader's Home says nothing of it.
        Assert.DoesNotContain("System Collapse", await (await factory.SignUpAsync("Other Everyday Reader")).GetStringAsync("/"));
    }

    [Fact]
    public async Task Each_connection_says_whether_it_is_set_up_and_when_it_was_last_heard_from()
    {
        var client = await factory.SignUpAsync("Connected Reader");
        var readerId = await factory.ReaderIdAsync("Connected Reader");
        var book = (await (await client.PostAsJsonAsync("/books", new CreateBookRequest("Tehanu", "Ursula K. Le Guin", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var all = await Connections.ForReaderAsync(db);
        Assert.Equal(["KOReader", "Kobo", "Audiobookshelf", "Readwise", "Hardcover", "Another shelf"], all.Select(connection => connection.Name));
        Assert.All(all, connection => Assert.Equal(ConnectionHealth.Off, connection.Health));

        // Set up, and quiet: waiting, not trouble.
        var reader = await db.Readers.FindAsync(readerId);
        reader!.KosyncHash = Kosync.HashOf(Kosync.NewPassword());
        await db.SaveChangesAsync();
        Assert.Equal(ConnectionHealth.Waiting, (await Connections.ForReaderAsync(db)).Single(connection => connection.Name == "KOReader").Health);

        // Heard from: working, with when and from what.
        db.KosyncPlaces.Add(new KosyncPlace { ReaderId = readerId, BookId = book.Id, Progress = "/body/p[1]", Percentage = 0.2, Device = "Kindle Oasis", UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var koreader = (await Connections.ForReaderAsync(db)).Single(connection => connection.Name == "KOReader");
        Assert.Equal(ConnectionHealth.Working, koreader.Health);
        Assert.Contains("Kindle Oasis", koreader.Note);

        Assert.Contains("Connections", await client.GetStringAsync("/sync"));
        Assert.Contains("Kindle Oasis", await client.GetStringAsync("/sync"));
    }
}
