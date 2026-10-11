using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class WelcomeAndCardTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_new_shelf_offers_the_ways_to_bring_a_library()
    {
        var reader = await factory.SignUpAsync("Newcomer");
        var home = await reader.GetStringAsync("/");
        Assert.Contains("Where are your books now?", home);
        foreach (var way in new[] { "/import/audiobookshelf", "/import/calibre", "/backup#csv", "/free" })
        {
            Assert.Contains($"href=\"{way}\"", home);
        }

        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Kindred", "Octavia E. Butler", BookStatus.Reading, null), JsonOptions);
        Assert.DoesNotContain("Where are your books now?", await reader.GetStringAsync("/"));
    }

    [Fact]
    public async Task The_year_becomes_a_picture_with_its_numbers_and_books()
    {
        var reader = await factory.SignUpAsync("Card Reader");
        var year = DateTime.UtcNow.Year;
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("The Dispossessed", "Ursula K. Le Guin", BookStatus.Finished, 5, Pages: 387, FinishedOn: new DateOnly(year, 2, 3)), JsonOptions);
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("A Very Long Title That Goes On and On Without Any Cover At All", "Someone", BookStatus.Finished, null, Pages: 120, FinishedOn: new DateOnly(year, 3, 4)), JsonOptions);

        var response = await reader.GetAsync($"/books/years/{year}/card.svg");
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        var svg = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("<svg", svg);
        Assert.Contains($"My {year} in books", svg);
        Assert.Contains("books finished", svg);
        Assert.Contains("507", svg);
        Assert.Contains("The Dispossessed", svg);
        Assert.DoesNotContain("foreignObject", svg);
        Assert.Contains("Share my year", await reader.GetStringAsync($"/years/{year}"));
    }

    [Fact]
    public void A_spine_title_wraps_and_is_cut_short_when_it_runs_over()
    {
        Assert.Equal(["A Wizard of", "Earthsea"], YearCard.Wrap("A Wizard of Earthsea", 13, 6));
        var long_ = YearCard.Wrap(string.Join(' ', Enumerable.Repeat("word", 40)), 13, 3);
        Assert.Equal(3, long_.Count);
        Assert.EndsWith("…", long_[^1]);
    }
}
