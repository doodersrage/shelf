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

    // The player moves the place back or forward as the listener does; a place sent late, from listening offline,
    // moves it on only, so a phone catching up never undoes listening done elsewhere since. Its time still counts.
    [Fact]
    public async Task A_place_sent_late_from_listening_offline_never_sends_the_listener_back()
    {
        var listener = await factory.SignUpAsync("Late Phone Listener");
        var book = await WithAudioAsync(listener, "Heard Offline");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        async Task<(int?, int?)> PlaceAsync()
        {
            var place = (await listener.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!;
            return (place.AudioTrack, place.AudioSeconds);
        }

        // Listened on to 3:00 here; the phone, offline, stopped at 1:00 and says so late.
        Assert.Equal(HttpStatusCode.NoContent, (await listener.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 180), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await listener.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 60, 20, today, Replayed: true), JsonOptions)).StatusCode);
        Assert.Equal((0, 180), await PlaceAsync());
        var positions = (await listener.GetFromJsonAsync<PositionsOfBook>($"/books/{book.Id}/positions", JsonOptions))!;
        Assert.False(Assert.Single(positions.Reports, report => report.Device == "listening offline").Kept);

        // Sent late and further on, it moves the place; and the player itself still goes back when asked.
        await listener.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 240, Replayed: true), JsonOptions);
        Assert.Equal((0, 240), await PlaceAsync());
        await listener.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 30), JsonOptions);
        Assert.Equal((0, 30), await PlaceAsync());
    }

    // A player tab left open says where it last knew the place. Moved since on another device, the place stays, the
    // tab is told where it is now, and its listening time still counts; told again from there, it moves as before.
    [Fact]
    public async Task A_player_left_open_cannot_send_back_a_place_moved_on_elsewhere()
    {
        var listener = await factory.SignUpAsync("Stale Tab Listener");
        var book = await WithAudioAsync(listener, "Heard in Two Rooms");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        async Task<(int?, int?)> PlaceAsync()
        {
            var place = (await listener.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!;
            return (place.AudioTrack, place.AudioSeconds);
        }

        // Both tabs knew 1:00. The phone listens on to 5:00; the old tab, playing again from 1:00, says 1:15.
        Assert.Equal(HttpStatusCode.NoContent, (await listener.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 60), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await listener.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 300, From: new AudioSpot(0, 60.4)), JsonOptions)).StatusCode);
        var stale = await listener.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 75, 15, today, From: new AudioSpot(0, 60)), JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(new AudioSpot(0, 300), await stale.Content.ReadFromJsonAsync<AudioSpot>(JsonOptions));
        Assert.Equal((0, 300), await PlaceAsync());
        Assert.Equal(15, (await listener.GetFromJsonAsync<ListeningSummary>("/books/listening", JsonOptions))!.TodaySeconds);

        // From the place it was told, the tab moves it again, back as well; and a save that says nothing moves it as before.
        Assert.Equal(HttpStatusCode.NoContent, (await listener.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 90, From: new AudioSpot(0, 300)), JsonOptions)).StatusCode);
        Assert.Equal((0, 90), await PlaceAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await listener.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(0, 20), JsonOptions)).StatusCode);
        Assert.Equal((0, 20), await PlaceAsync());
    }

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
    public async Task Time_listened_offline_arrives_later_on_the_day_it_was()
    {
        var reader = await factory.SignUpAsync("Offline Listener");
        var book = await WithAudioAsync(reader, "Heard on a Train");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // An afternoon with no connection, sent the next day: up to a day's worth, from the last month.
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync($"/books/{book.Id}/audio/listened", new ListenedRequest(today.AddDays(-3), 2 * 60 * 60), JsonOptions)).StatusCode);
        await reader.PostAsJsonAsync($"/books/{book.Id}/audio/listened", new ListenedRequest(today.AddDays(-3), 99 * 60 * 60), JsonOptions);
        var summary = await reader.GetFromJsonAsync<ListeningSummary>("/books/listening", JsonOptions);
        Assert.Equal(2 * 60 * 60 + 24 * 60 * 60, summary!.LastDays.Single(day => day.Day == today.AddDays(-3)).Seconds);
        Assert.Equal(0, summary.TodaySeconds);
        Assert.Equal(HttpStatusCode.NotFound, (await (await factory.SignUpAsync("Not Listening")).PostAsJsonAsync($"/books/{book.Id}/audio/listened", new ListenedRequest(today, 60), JsonOptions)).StatusCode);
    }

    [Fact]
    public async Task Time_in_the_reader_is_kept_by_day_and_keeps_the_streak()
    {
        var reader = await factory.SignUpAsync("Timed Reader");
        var book = await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Read Slowly", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync($"/books/{book!.Id}/reading-time", new ReadingTimeRequest(today, 60), JsonOptions)).StatusCode);
        // More than five minutes at once is not believed.
        await reader.PostAsJsonAsync($"/books/{book.Id}/reading-time", new ReadingTimeRequest(today, 5000), JsonOptions);
        var summary = await reader.GetFromJsonAsync<ListeningSummary>("/books/reading-time", JsonOptions);
        Assert.Equal(360, summary!.TodaySeconds);
        Assert.Equal("Read Slowly", Assert.Single(summary.TopBooks).Title);
        Assert.Equal(HttpStatusCode.NotFound, (await (await factory.SignUpAsync("Not Reading")).PostAsJsonAsync($"/books/{book.Id}/reading-time", new ReadingTimeRequest(today, 60), JsonOptions)).StatusCode);

        var stats = await reader.GetStringAsync("/stats");
        Assert.Contains("Reading time", stats);
        Assert.Contains("You read today or yesterday.", stats);
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
