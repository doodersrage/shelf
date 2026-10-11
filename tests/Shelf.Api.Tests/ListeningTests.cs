using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class ListeningTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Time_listened_is_kept_by_day_for_each_listener()
    {
        var owner = await factory.SignUpAsync("Listening Owner");
        var borrower = await factory.SignUpAsync("Listening Borrower");
        var book = await WithAudioAsync(owner, "Heard Aloud");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await owner.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 30, 15, today), JsonOptions);
        await owner.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 45, 15, today), JsonOptions);
        // More than two minutes at once is not believed, and a date far from today is taken as today.
        await owner.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 60, 9999, today.AddDays(-30)), JsonOptions);
        await owner.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 60, 60, today.AddDays(-1)), JsonOptions);

        var summary = await owner.GetFromJsonAsync<ListeningSummary>("/books/listening", JsonOptions);
        Assert.Equal(150, summary!.TodaySeconds);
        Assert.Equal(210, summary.WeekSeconds);
        Assert.Equal(60, summary.LastDays[^2].Seconds);
        Assert.Equal(("Heard Aloud", 210), (Assert.Single(summary.TopBooks).Title, summary.TopBooks[0].Seconds));

        // A borrower's time is their own, and counts on a book that is not on their shelf.
        var borrowerId = await factory.ReaderIdAsync("Listening Borrower");
        await owner.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(borrowerId), JsonOptions);
        await borrower.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 10, 40, today), JsonOptions);
        Assert.Equal(40, (await borrower.GetFromJsonAsync<ListeningSummary>("/books/listening", JsonOptions))!.TodaySeconds);
        Assert.Equal(150, (await owner.GetFromJsonAsync<ListeningSummary>("/books/listening", JsonOptions))!.TodaySeconds);

        // The stats page shows it, and a day of listening keeps the streak.
        var stats = await owner.GetStringAsync("/stats");
        Assert.Contains("Listening", stats);
        Assert.Contains("2 days of reading in a row.", stats);
    }

    [Fact]
    public async Task The_end_of_the_recording_finishes_the_owners_book_but_not_for_a_borrower()
    {
        var owner = await factory.SignUpAsync("Finishing Owner");
        var borrower = await factory.SignUpAsync("Finishing Borrower");
        var book = await WithAudioAsync(owner, "Heard to the End");
        var lent = await WithAudioAsync(owner, "Lent and Heard");
        await owner.PostAsJsonAsync($"/books/{lent.Id}/lend", new LendRequest(await factory.ReaderIdAsync("Finishing Borrower")), JsonOptions);

        var first = await (await owner.PostAsync($"/books/{book.Id}/audio/finished", null)).Content.ReadFromJsonAsync<FinishedResponse>(JsonOptions);
        Assert.True(first!.Marked);
        var finished = await owner.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions);
        Assert.Equal(BookStatus.Finished, finished!.Status);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), finished.FinishedOn);
        Assert.False((await (await owner.PostAsync($"/books/{book.Id}/audio/finished", null)).Content.ReadFromJsonAsync<FinishedResponse>(JsonOptions))!.Marked);

        Assert.False((await (await borrower.PostAsync($"/books/{lent.Id}/audio/finished", null)).Content.ReadFromJsonAsync<FinishedResponse>(JsonOptions))!.Marked);
        Assert.Equal(BookStatus.Reading, (await owner.GetFromJsonAsync<BookResponse>($"/books/{lent.Id}", JsonOptions))!.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await borrower.PostAsync($"/books/{book.Id}/audio/finished", null)).StatusCode);
    }

    private static async Task<BookResponse> WithAudioAsync(HttpClient client, string title)
    {
        var book = await (await client.PostAsJsonAsync("/books", new CreateBookRequest(title, "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        using var content = new MultipartFormDataContent { { new StreamContent(BooksEndpointTests.ZipText("01.mp3", "a track")), "file", "heard.zip" } };
        await client.PostAsync($"/books/{book!.Id}/audio", content);
        return book;
    }
}
