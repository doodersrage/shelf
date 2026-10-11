using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class YearReviewTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_year_in_books_sums_up_what_was_finished_and_what_stood_out()
    {
        var reader = await factory.SignUpAsync("Reviewing Reader");
        var year = DateTime.UtcNow.Year;
        async Task<BookResponse> Finish(string title, string author, int pages, string date, int? rating = null, bool loved = false, string[]? tags = null, BookFormat? format = null) =>
            (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest(title, author, BookStatus.Finished, rating, Pages: pages, FinishedOn: DateOnly.Parse(date), Loved: loved, Tags: tags, Format: format), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;

        await Finish("Earlier Book", "Ursula K. Le Guin", 200, $"{year - 1}-06-01");
        await Finish("The Dispossessed", "Ursula K. Le Guin", 387, $"{year}-02-10", 5, tags: ["sf"], format: BookFormat.Paperback);
        var longest = await Finish("Middlemarch", "George Eliot", 880, $"{year}-03-02", 4, loved: true, tags: ["classic"], format: BookFormat.Ebook);
        await Finish("Piranesi", "Susanna Clarke", 272, $"{year}-03-20", tags: ["sf"], format: BookFormat.Paperback);
        await reader.PostAsJsonAsync($"/books/{longest.Id}/quotes", new { text = "It is never too late to be what you might have been.", page = 1 }, JsonOptions);
        await reader.PostAsJsonAsync($"/books/{longest.Id}/reading-time", new ReadingTimeRequest(null, 120), JsonOptions);

        var review = await reader.GetFromJsonAsync<YearReviewResponse>($"/books/years/{year}/review", JsonOptions);
        Assert.Equal((3, 387 + 880 + 272, 3, 2), (review!.Finished, review.Pages, review.Authors, review.NewAuthors));
        Assert.Equal(2, review.ByMonth[2]);
        Assert.Equal(("Middlemarch", "Piranesi"), (review.Longest!.Title, review.Shortest!.Title));
        Assert.Equal(("The Dispossessed", "Piranesi"), (review.First!.Title, review.Last!.Title));
        Assert.Equal(["The Dispossessed", "Middlemarch"], review.Loved.Select(book => book.Title));
        Assert.Equal(("sf", 2), (review.Tags[0].Name, review.Tags[0].Count));
        Assert.Equal(("Paperback", 2), (review.Formats[0].Name, review.Formats[0].Count));
        Assert.Equal((1, "Middlemarch", 120), (review.Quotes, review.FavouriteQuoteBook, review.ReadingSeconds));

        var page = await reader.GetStringAsync($"/years/{year}");
        Assert.Contains($"Your {year} in books", page);
        Assert.Contains("Your busiest month was March.", page);
        Assert.Contains("It is never too late", page);
        Assert.Contains("Nothing finished in 1999", await reader.GetStringAsync("/years/1999"));
    }
}
