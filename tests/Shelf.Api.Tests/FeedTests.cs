using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class FeedTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_calendar_address_lists_the_loans_due_dates()
    {
        var owner = await factory.SignUpAsync("Calendar Owner");
        var ownerId = await factory.ReaderIdAsync("Calendar Owner");
        var book = (await (await owner.PostAsJsonAsync("/books", new CreateBookRequest("Lent, Out", "Someone", BookStatus.Finished, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        await owner.PutAsJsonAsync($"/books/{book.Id}", new UpdateBookRequest("Lent, Out", "Someone", BookStatus.Finished, null, LoanedTo: "Ged", DueOn: new DateOnly(2030, 3, 4)), JsonOptions);

        string token;
        await using (var scope = Scope(ownerId))
        {
            token = await Feeds.MakeCalendarAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>());
        }

        var anyone = factory.CreateClient();
        var ics = await anyone.GetAsync($"/calendar/{token}.ics");
        Assert.Equal("text/calendar", ics.Content.Headers.ContentType!.MediaType);
        var text = await ics.Content.ReadAsStringAsync();
        Assert.Contains("DTSTART;VALUE=DATE:20300304", text, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Lent\\, Out is due back from Ged", text, StringComparison.Ordinal);

        // A new address replaces the old.
        await using (var scope = Scope(ownerId))
        {
            await Feeds.MakeCalendarAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"/calendar/{token}.ics")).StatusCode);
    }

    [Fact]
    public async Task A_public_page_shows_what_is_read_and_finished_and_nothing_private()
    {
        var reader = await factory.SignUpAsync("Public Reader");
        var readerId = await factory.ReaderIdAsync("Public Reader");
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Reading Now", "Author One", BookStatus.Reading, null, Notes: "A private note"), JsonOptions);
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Done", "Author Two", BookStatus.Finished, 4, FinishedOn: new DateOnly(2026, 2, 1)), JsonOptions);
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Someday", "Author Three", BookStatus.Want, null), JsonOptions);

        string token;
        await using (var scope = Scope(readerId))
        {
            token = (await Feeds.SetPublicAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), true))!;
        }

        var anyone = factory.CreateClient();
        var shelf = await anyone.GetFromJsonAsync<PublicShelf>($"/public/{token}/shelf.json", JsonOptions);
        Assert.Equal("Public Reader", shelf!.Reader);
        Assert.Equal(["Reading Now"], shelf.Reading.Select(book => book.Title));
        Assert.Equal(["Done"], shelf.Finished.Select(book => book.Title));
        var page = await anyone.GetStringAsync($"/public/{token}");
        Assert.Contains("Reading Now", page, StringComparison.Ordinal);
        Assert.DoesNotContain("A private note", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Someday", page, StringComparison.Ordinal);
        Assert.Contains("Reading Now", await anyone.GetStringAsync($"/public/{token}/widget"), StringComparison.Ordinal);
        var feed = await anyone.GetStringAsync($"/public/{token}/feed.xml");
        Assert.Contains("Done, by Author Two", feed, StringComparison.Ordinal);
        Assert.Contains("4 of 5 stars", feed, StringComparison.Ordinal);

        await using (var scope = Scope(readerId))
        {
            Assert.Null(await Feeds.SetPublicAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), false));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"/public/{token}/shelf.json")).StatusCode);
        Assert.Contains("This page is not shared", await anyone.GetStringAsync($"/public/{token}"), StringComparison.Ordinal);
    }

    private AsyncServiceScope Scope(int readerId)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope;
    }
}
