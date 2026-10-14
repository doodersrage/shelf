using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Components.Layout;
using Shelf.Api.Data;

namespace Shelf.Api.Tests;

// Finding one's way: quick jump to any page or any of one's own things, a menu with the unused pages left out, the
// long pages in tabs that links into their sections still open, and every way to add books in one place.
public sealed class NavigationTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Quick_jump_finds_pages_by_name_or_purpose_and_a_reader_s_own_things()
    {
        var reader = await factory.SignUpAsync("Jumping Reader");
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Piranesi", "Susanna Clarke", BookStatus.Finished, null, Series: "Standalone Wonders"), JsonOptions);
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Jonathan Strange & Mr Norrell", "Susanna Clarke", BookStatus.Want, null), JsonOptions);
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("The 100% Book", "Someone Else", BookStatus.Want, null), JsonOptions);

        // A page by its name, and by what it's for.
        Assert.Contains(await JumpAsync(reader, "trash"), hit => hit.Href == "/trash");
        Assert.Contains(await JumpAsync(reader, "goodreads"), hit => hit.Href == "/add");
        Assert.Contains(await JumpAsync(reader, "two"), hit => hit.Href == "/account#two-step");

        // Books by the start of a title first, authors, and series.
        var found = await JumpAsync(reader, "pira");
        Assert.Equal("/library/", found.First(hit => hit.Href.StartsWith("/library/", StringComparison.Ordinal)).Href[..9]);
        Assert.Contains(found, hit => hit.Title == "Piranesi" && hit.Detail == "Susanna Clarke");
        Assert.Contains(await JumpAsync(reader, "clarke"), hit => hit.Href == "/library?author=Susanna%20Clarke");
        Assert.Contains(await JumpAsync(reader, "wonders"), hit => hit.Href == "/library?series=Standalone%20Wonders");

        // What's typed is a word, not a pattern.
        Assert.Equal(["The 100% Book"], (await JumpAsync(reader, "100%")).Where(hit => hit.Href.StartsWith("/library/", StringComparison.Ordinal)).Select(hit => hit.Title));
        Assert.DoesNotContain(await JumpAsync(reader, "_"), hit => hit.Href.StartsWith("/library/", StringComparison.Ordinal));

        // Another reader's books, and admin pages, are never offered.
        var other = await factory.SignUpAsync("Other Jumper");
        Assert.DoesNotContain(await JumpAsync(other, "pira"), hit => hit.Title == "Piranesi");
        Assert.DoesNotContain(await JumpAsync(other, "health"), hit => hit.Href == "/admin/health");
        Assert.Contains(await JumpAsync(factory.Client, "health"), hit => hit.Href == "/admin/health");

        // With nothing typed, the main places.
        Assert.Equal("/", (await JumpAsync(reader, ""))[0].Href);
    }

    [Fact]
    public async Task Pages_left_out_of_the_menu_are_gone_from_it_but_not_from_quick_jump()
    {
        var reader = await factory.SignUpAsync("Menu Trimmer");
        Assert.Contains("href=\"/podcasts\"", await reader.GetStringAsync("/library"));

        var id = await factory.ReaderIdAsync("Menu Trimmer");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var me = await db.Readers.IgnoreQueryFilters().FirstAsync(item => item.Id == id);
            // Settings can't be left out, nor an address that isn't a page.
            me.HiddenNav = NavMap.Store(["/podcasts", "/clubs", "/account", "/nowhere"]);
            await db.SaveChangesAsync();
            Assert.Equal("/clubs\n/podcasts", me.HiddenNav);
        }

        var page = await reader.GetStringAsync("/library");
        Assert.DoesNotContain("href=\"/podcasts\"", page);
        Assert.DoesNotContain("href=\"/clubs\"", page);
        Assert.Contains("href=\"/account\"", page);
        Assert.Contains(await JumpAsync(reader, "podcasts"), hit => hit.Href == "/podcasts");
    }

    [Fact]
    public async Task Long_pages_open_on_the_tab_their_address_names()
    {
        var reader = await factory.SignUpAsync("Tab Reader");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Tabbed", "Someone", BookStatus.Reading, null, Notes: "A note kept in the details"), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;

        var reading = await reader.GetStringAsync($"/library/{book.Id}");
        Assert.Contains("id=\"reading\"", reading);
        Assert.DoesNotContain("id=\"details\"", reading);
        var details = await reader.GetStringAsync($"/library/{book.Id}?tab=tab-details");
        Assert.Contains("id=\"details\"", details);
        Assert.DoesNotContain("id=\"reading\"", details);

        Assert.Contains("id=\"look\"", await reader.GetStringAsync("/account"));
        var security = await reader.GetStringAsync("/account?tab=tab-security");
        Assert.Contains("id=\"passkeys\"", security);
        Assert.DoesNotContain("id=\"look\"", security);
    }

    [Fact]
    public async Task Every_way_to_add_books_is_on_one_page()
    {
        var reader = await factory.SignUpAsync("Adding Reader");
        var page = await reader.GetStringAsync("/add");
        foreach (var href in new[] { "/library?add=1", "/scan", "/backup#csv", "/import/spreadsheet", "/import/calibre", "/import/audiobookshelf", "/find", "/free" })
        {
            Assert.Contains($"href=\"{href}\"", page);
        }

        // A new shelf's welcome leads there too.
        Assert.Contains("href=\"/add\"", await reader.GetStringAsync("/"));
    }

    private static async Task<JumpHit[]> JumpAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/books/jump?q={Uri.EscapeDataString(query)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JumpHit[]>(JsonOptions))!;
    }
}
