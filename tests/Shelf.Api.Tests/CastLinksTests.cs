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

    // A book shared with a household member casts for them as for its owner, and its addresses stop when they're
    // let go; one who may not download gets none, as an address that needs no sign-in is a download.
    [Fact]
    public async Task A_household_member_casts_shared_books_as_their_download_permission_allows()
    {
        var owner = await factory.SignUpAsync("Cast Owner");
        var member = await factory.SignUpAsync("Cast Member");
        var book = (await (await owner.PostAsJsonAsync("/books", new CreateBookRequest("Cast in the Den", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(BooksEndpointTests.ZipText("01.mp3", "a shared track to cast")), "file", "den.zip" } })
        {
            await owner.PostAsync($"/books/{book.Id}/audio", content);
        }

        var collection = (await (await owner.PostAsJsonAsync("/books/collections", new CollectionRequest("Den"), JsonOptions)).Content.ReadFromJsonAsync<CollectionResponse>(JsonOptions))!;
        await owner.PostAsJsonAsync($"/books/collections/{collection.Id}/books", new CollectionBookRequest(book.Id), JsonOptions);
        await owner.PostAsJsonAsync($"/books/collections/{collection.Id}/members", new MemberRequest(await factory.ReaderIdAsync("Cast Member")), JsonOptions);

        var plan = (await member.GetFromJsonAsync<CastPlan>($"/books/{book.Id}/audio/cast", JsonOptions))!;
        var device = factory.CreateClient();
        Assert.Equal("a shared track to cast", await device.GetStringAsync(plan.Tracks[0]));

        var membership = Assert.Single((await owner.GetFromJsonAsync<HouseholdMember[]>($"/books/collections/{collection.Id}/members", JsonOptions))!);
        await owner.PutAsJsonAsync($"/books/collections/{collection.Id}/members/{membership.Id}", new MemberChange(CanDownload: false, HideExplicit: false), JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/books/{book.Id}/audio/cast")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await device.GetAsync(plan.Tracks[0])).StatusCode);
    }
}
