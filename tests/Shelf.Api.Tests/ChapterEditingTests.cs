using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class ChapterEditingTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures"));

    [Fact]
    public async Task Chapters_set_by_hand_are_placed_across_the_tracks_until_forgotten()
    {
        var reader = await factory.SignUpAsync("Chapter Editor");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Three Tracks", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new StreamContent(File.OpenRead(Path.Combine(Fixtures, "three-tracks.zip"))), "file", "three-tracks.zip" } })
        {
            (await reader.PostAsync($"/books/{book.Id}/audio", content)).EnsureSuccessStatusCode();
        }

        // Out of order, they are put in order; a start past the end, one name missing, or one chapter are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PutAsJsonAsync($"/books/{book.Id}/audio/chapters", new[] { new ChapterMark("Only", 0) }, JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PutAsJsonAsync($"/books/{book.Id}/audio/chapters", new[] { new ChapterMark("One", 0), new ChapterMark("Late", 99) }, JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PutAsJsonAsync($"/books/{book.Id}/audio/chapters", new[] { new ChapterMark("One", 0), new ChapterMark(" ", 5) }, JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync($"/books/{book.Id}/audio/chapters", new[] { new ChapterMark("Second", 6), new ChapterMark("First", 0) }, JsonOptions)).StatusCode);

        var plan = await reader.GetFromJsonAsync<AudioPlan>($"/books/{book.Id}/audio/plan", JsonOptions);
        Assert.Equal([("First", 0), ("Second", 1)], plan!.Chapters.Select(chapter => (chapter.Title, chapter.Track)));

        var stranger = await factory.SignUpAsync("Chapter Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"/books/{book.Id}/audio/chapters", new[] { new ChapterMark("A", 0), new ChapterMark("B", 5) }, JsonOptions)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/{book.Id}/audio/chapters")).StatusCode);
        Assert.Equal(3, (await reader.GetFromJsonAsync<AudioPlan>($"/books/{book.Id}/audio/plan", JsonOptions))!.Chapters.Length);
    }

    [Theory]
    [InlineData("1:02:03", 3723)]
    [InlineData("62:03", 3723)]
    [InlineData("90.5", 90.5)]
    public void Times_are_read_as_people_type_them(string text, double seconds) => Assert.Equal(seconds, ChapterEditing.ParseClock(text));
}
