using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

// A Kobo's side of the store protocol, as Calibre-Web documents what the device asks and expects.
public sealed class KoboTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_kobo_syncs_the_readers_epubs_downloads_them_and_sends_its_place_back()
    {
        var reader = await factory.SignUpAsync("Kobo Reader");
        var readerId = await factory.ReaderIdAsync("Kobo Reader");
        var sea = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("A Sea Story", "Herman Example", BookStatus.Want, null, Series: "Sea", SeriesNumber: 2), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(KoreaderHighlightsTests.Epub()), "file", "sea.epub" } })
        {
            Assert.True((await reader.PostAsync($"/books/{sea.Id}/ebook", content)).IsSuccessStatusCode);
        }

        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Paper Only", "Someone", BookStatus.Want, null), JsonOptions);
        string token;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            token = await Kobo.MakeTokenAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>());
        }

        var kobo = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await kobo.GetAsync("/kobo/wrong/v1/initialization")).StatusCode);

        // Signing in, and the store's addresses, with Shelf's own for the library and covers.
        using var asking = new StringContent("""{"AffiliateName":"Kobo","DeviceId":"kobo-1","UserKey":"abc"}""", Encoding.UTF8, "application/json");
        var auth = await (await kobo.PostAsync($"/kobo/{token}/v1/auth/device", asking)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(("Bearer", "abc"), (auth!["TokenType"]!.GetValue<string>(), auth["UserKey"]!.GetValue<string>()));
        var resources = (await kobo.GetFromJsonAsync<JsonObject>($"/kobo/{token}/v1/initialization"))!["Resources"]!;
        Assert.EndsWith($"/kobo/{token}/v1/library/sync", resources["library_sync"]!.GetValue<string>());
        Assert.Contains($"/kobo/{token}/{{ImageId}}", resources["image_url_template"]!.GetValue<string>());
        Assert.Equal("{}", await kobo.GetStringAsync($"/kobo/{token}/v1/user/profile"));

        // The first sync: the EPUB, not the paper book, with a download and its series.
        var first = await kobo.GetAsync($"/kobo/{token}/v1/library/sync");
        Assert.True(first.Headers.Contains("x-kobo-synctoken"));
        var entitlement = Assert.Single(JsonNode.Parse(await first.Content.ReadAsStringAsync())!.AsArray())!["NewEntitlement"]!;
        var metadata = entitlement["BookMetadata"]!;
        var uuid = Kobo.UuidOf(sea.Id).ToString();
        Assert.Equal((uuid, "A Sea Story", "Sea"), (entitlement["BookEntitlement"]!["Id"]!.GetValue<string>(), metadata["Title"]!.GetValue<string>(), metadata["Series"]!["Name"]!.GetValue<string>()));
        Assert.Equal("ReadyToRead", entitlement["ReadingState"]!["StatusInfo"]!["Status"]!.GetValue<string>());
        var download = new Uri(metadata["DownloadUrls"]![0]!["Url"]!.GetValue<string>()).PathAndQuery;
        var file = await kobo.GetAsync(download);
        Assert.Equal(("application/epub+zip", KoreaderHighlightsTests.Epub().Length > 0), (file.Content.Headers.ContentType!.MediaType, (await file.Content.ReadAsByteArrayAsync()).Length > 0));

        // The Kobo reads into the second chapter: Shelf's place and status follow.
        var state = new { ReadingStates = new[] { new { EntitlementId = uuid, CurrentBookmark = new { ProgressPercent = 50, Location = new { Source = "c2.xhtml", Type = "KoboSpan", Value = "kobo.1.1" } }, StatusInfo = new { Status = "Reading" } } } };
        var put = await kobo.PutAsync($"/kobo/{token}/v1/library/{uuid}/state", new StringContent(JsonSerializer.Serialize(state), Encoding.UTF8, "application/json"));
        Assert.Equal("Success", (await put.Content.ReadFromJsonAsync<JsonObject>())!["RequestResult"]!.GetValue<string>());
        var after = (await reader.GetFromJsonAsync<BookResponse>($"/books/{sea.Id}", JsonOptions))!;
        Assert.Equal(BookStatus.Reading, after.Status);
        Assert.Equal(1, (await reader.GetFromJsonAsync<PlaceResponse>($"/books/{sea.Id}/place", JsonOptions))!.EbookChapter);
        var kept = (await kobo.GetFromJsonAsync<JsonArray>($"/kobo/{token}/v1/library/{uuid}/state"))![0]!;
        Assert.Equal("kobo.1.1", kept["CurrentBookmark"]!["Location"]!["Value"]!.GetValue<string>());

        // Again it is a change, not a new book; once off the shelf, the Kobo is told to take it away.
        Assert.NotNull(JsonNode.Parse(await kobo.GetStringAsync($"/kobo/{token}/v1/library/sync"))!.AsArray()[0]!["ChangedEntitlement"]);
        await reader.DeleteAsync($"/books/{sea.Id}");
        var gone = JsonNode.Parse(await kobo.GetStringAsync($"/kobo/{token}/v1/library/sync"))!.AsArray();
        Assert.True(Assert.Single(gone)!["ChangedEntitlement"]!["BookEntitlement"]!["IsRemoved"]!.GetValue<bool>());
        Assert.Empty(JsonNode.Parse(await kobo.GetStringAsync($"/kobo/{token}/v1/library/sync"))!.AsArray());
    }
}
