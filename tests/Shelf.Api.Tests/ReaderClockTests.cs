using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class ReaderClockTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // Whatever the hour in UTC, one of these is on another day: fourteen hours ahead, or eleven behind.
    private static string ZoneOnAnotherDay()
    {
        var utc = DateOnly.FromDateTime(DateTime.UtcNow);
        return ReaderClock.Today("Pacific/Kiritimati") != utc ? "Pacific/Kiritimati" : "Pacific/Pago_Pago";
    }

    [Fact]
    public void A_day_is_the_day_where_the_reader_is()
    {
        var zone = ZoneOnAnotherDay();
        Assert.NotEqual(DateOnly.FromDateTime(DateTime.UtcNow), ReaderClock.Today(zone));
        Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).DateTime), ReaderClock.Today(null));
        Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).DateTime), ReaderClock.Today("Not/AZone"));
        Assert.True(ReaderClock.IsZone("America/Chicago"));
        Assert.False(ReaderClock.IsZone("Not/AZone"));
    }

    // The browser's guess is kept only until the reader chooses; a choice is kept; a name that isn't a zone is
    // refused; and a book finished is finished on the reader's day, not the server's.
    [Fact]
    public async Task A_reader_keeps_a_time_zone_and_their_days_follow_it()
    {
        var reader = await factory.SignUpAsync("Clock Reader");
        var zone = ZoneOnAnotherDay();
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync("/settings/timezone", new TimeZoneRequest(zone, OnlyIfUnset: true), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync("/settings/timezone", new TimeZoneRequest("Europe/London", OnlyIfUnset: true), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PutAsJsonAsync("/settings/timezone", new TimeZoneRequest("Not/AZone"), JsonOptions)).StatusCode);
        Assert.Contains($"value=\"{zone}\" selected", (await reader.GetStringAsync("/account")).Replace("selected=\"\"", "selected"));

        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Read Late", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        await reader.PutAsJsonAsync($"/books/{book.Id}", new UpdateBookRequest("Read Late", "Someone", BookStatus.Finished, null), JsonOptions);
        Assert.Equal(ReaderClock.Today(zone), (await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!.FinishedOn);
    }
}
