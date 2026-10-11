using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class M4bTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures"));

    [Fact]
    public async Task Three_tracks_become_one_m4b_with_their_chapters_which_can_take_their_place()
    {
        if (!factory.Services.GetRequiredService<M4bService>().Available)
        {
            // ffmpeg is not here to make one.
            return;
        }

        var reader = await factory.SignUpAsync("M4B Maker");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Three Parts", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(File.OpenRead(Path.Combine(Fixtures, "three-tracks.zip"))), "file", "three-tracks.zip" } })
        {
            (await reader.PostAsync($"/books/{book.Id}/audio", content)).EnsureSuccessStatusCode();
        }

        await reader.PutAsJsonAsync($"/books/{book.Id}/audio/place", new AudioPlaceRequest(1, 2), JsonOptions);
        var status = await (await reader.PostAsync($"/books/{book.Id}/audio/m4b", null)).Content.ReadFromJsonAsync<M4bStatus>(JsonOptions);
        for (var i = 0; i < 150 && status!.State == M4bState.Making; i++)
        {
            await Task.Delay(100);
            status = await reader.GetFromJsonAsync<M4bStatus>($"/books/{book.Id}/audio/m4b", JsonOptions);
        }

        Assert.Equal(M4bState.Ready, status!.State);
        var file = await reader.GetAsync($"/books/{book.Id}/audio/m4b/file");
        Assert.Equal("audio/mp4", file.Content.Headers.ContentType!.MediaType);

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync($"/books/{book.Id}/audio/m4b/use", null)).StatusCode);
        var plan = await reader.GetFromJsonAsync<AudioPlan>($"/books/{book.Id}/audio/plan", JsonOptions);
        Assert.Single(plan!.Tracks);
        Assert.InRange(plan.Tracks[0].Length ?? 0, 11, 13.5);
        Assert.Equal(3, plan.Chapters.Length);
        // The place moved with it: two seconds into the second track is past the first track's three.
        Assert.Equal((0, true), (plan.Track, plan.Seconds >= 4));
    }
}
