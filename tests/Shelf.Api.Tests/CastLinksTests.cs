using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class CastLinksTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // A cast device fetches the tracks with no sign-in, by addresses signed for this reader and recording; they open
    // nothing else, a changed address opens nothing, and they stop with the recording.
    [Fact]
    public async Task A_cast_device_gets_signed_addresses_for_the_recording_alone()
    {
        var reader = await factory.SignUpAsync("Caster");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Cast Away", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(BooksEndpointTests.ZipText("01.mp3", "a track to cast")), "file", "cast.zip" } })
        {
            await reader.PostAsync($"/books/{book.Id}/audio", content);
        }

        var plan = (await reader.GetFromJsonAsync<CastPlan>($"/books/{book.Id}/audio/cast", JsonOptions))!;
        var device = factory.CreateClient();
        Assert.Equal("a track to cast", await device.GetStringAsync(Assert.Single(plan.Tracks)));
        Assert.Equal(HttpStatusCode.NotFound, (await device.GetAsync(plan.Tracks[0][..^1] + "5")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await device.GetAsync(plan.Tracks[0].Replace("/cast/", "/cast/x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync($"/books/{book.Id}/audio/cast")).StatusCode);

        var stranger = await factory.SignUpAsync("Cast Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/audio/cast")).StatusCode);

        await reader.DeleteAsync($"/books/{book.Id}/audio");
        Assert.Equal(HttpStatusCode.NotFound, (await device.GetAsync(plan.Tracks[0])).StatusCode);
    }
}
