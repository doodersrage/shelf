using System.Net;
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
        Assert.Equal(["finished", "glance", "next", "shared", "reading", "added"], sections.Select(section => section.Name));
        Assert.Equal([true, false, true, true, true, true], sections.Select(section => section.Shown));
    }

    // Home leads with what other readers share: the books they've lent this reader, ready to read, and what is new on
    // their open shelves, each a click from asking for it; with none of the owner's own reading, and nothing at all
    // for a reader nothing is shared with. Hidden from Arrange Home, it goes.
    [Fact]
    public async Task Home_shows_what_other_readers_share()
    {
        var reader = await factory.SignUpAsync("Home Sharer Reader");
        var lender = await factory.SignUpAsync("Home Sharer Lender");
        var lenderId = await factory.ReaderIdAsync("Home Sharer Lender");
        var readerId = await factory.ReaderIdAsync("Home Sharer Reader");
        var lent = (await (await lender.PostAsJsonAsync("/books", new CreateBookRequest("Lent For Home", "Someone", BookStatus.Finished, 5), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(KoreaderHighlightsTests.Epub()), "file", "lent.epub" } })
        {
            (await lender.PostAsync($"/books/{lent.Id}/ebook", content)).EnsureSuccessStatusCode();
        }

        await lender.PostAsJsonAsync("/books", new CreateBookRequest("Open For Home", "Someone Else", BookStatus.Reading, null, Notes: "The lender's private note."), JsonOptions);
        Assert.DoesNotContain("id=\"shared-heading\"", await reader.GetStringAsync("/"));

        // Lent a book, a reader with none of their own yet has it on Home, ready to read.
        (await lender.PostAsJsonAsync($"/books/{lent.Id}/lend", new LendRequest(readerId), JsonOptions)).EnsureSuccessStatusCode();
        var lentHome = WebUtility.HtmlDecode(await reader.GetStringAsync("/"));
        Assert.Contains("Lent to you", lentHome);
        Assert.DoesNotContain("New on open shelves", lentHome);

        await lender.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(true), JsonOptions);
        var home = WebUtility.HtmlDecode(await reader.GetStringAsync("/"));
        Assert.Contains("id=\"shared-heading\"", home);
        Assert.Contains("Lent For Home", home);
        Assert.Contains($"/library/{lent.Id}/read", home);
        Assert.Contains("Open For Home", home);
        Assert.Contains($"/shelves/{lenderId}?q=Open%20For%20Home", home);
        Assert.Contains("Home Sharer Lender's shelf", home);
        Assert.DoesNotContain("The lender's private note.", home);
        // A book lent to the reader is under Lent to you, not offered again as new on the shelf.
        Assert.DoesNotContain($"/shelves/{lenderId}?q=Lent%20For%20Home", home);

        // The lender, with nothing shared with them, has no such section; a reader who hides it from Home doesn't see it.
        Assert.DoesNotContain("id=\"shared-heading\"", await lender.GetStringAsync("/"));
        await ShelfApiFactory.PostFormAsync(reader, "/", "/books/home", new() { ["shown"] = "glance" });
        Assert.DoesNotContain("id=\"shared-heading\"", await reader.GetStringAsync("/"));
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
                [("glance", true), ("shared", false), ("reading", false), ("next", false), ("finished", false), ("added", false)],
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
