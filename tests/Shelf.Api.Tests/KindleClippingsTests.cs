using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class KindleClippingsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // As a Kindle writes it: a byte-order mark, Windows line ends, and its own languages for the details line.
    private const string Clippings = "﻿A Sea Story: A Novel (Example, Herman)\r\n- Your Highlight on page 3 | Location 40-42 | Added on Sunday, 5 October 2025 09:15:00\r\n\r\nThe sea was calm and the ship was slow.\r\n==========\r\n"
        + "A Sea Story: A Novel (Example, Herman)\r\n- Your Note on page 3 | Location 42 | Added on Sunday, 5 October 2025 09:16:00\r\n\r\nSlow ships, calm seas.\r\n==========\r\n"
        + "A Sea Story: A Novel (Example, Herman)\r\n- Your Bookmark on page 9 | Location 120 | Added on Sunday, 5 October 2025 09:20:00\r\n\r\n\r\n==========\r\n"
        + "Paper Only (Writer, Some)\r\n- Ihre Markierung auf Seite 12 | Position 180-82 | Hinzugefügt am Montag, 6. Oktober 2025 10:00:00\r\n\r\nWords from a book with no file.\r\n==========\r\n"
        + "Not Here At All (Nobody)\r\n- Your Highlight on Location 5-6 | Added on Monday, 6 October 2025 10:00:00\r\n\r\nLost words.\r\n==========\r\n";

    [Fact]
    public void A_clippings_file_is_read_in_its_languages()
    {
        var clippings = KindleClippings.Parse(Clippings);
        Assert.Equal(5, clippings.Count);
        Assert.Equal(("A Sea Story: A Novel", "Example, Herman", "highlight", 3, 40, 42), (clippings[0].Title, clippings[0].Author, clippings[0].Kind, clippings[0].Page, clippings[0].LocationStart, clippings[0].LocationEnd));
        Assert.Equal(("note", "Slow ships, calm seas."), (clippings[1].Kind, clippings[1].Text));
        Assert.Equal("bookmark", clippings[2].Kind);
        Assert.Equal(("highlight", 12, 180, 182), (clippings[3].Kind, clippings[3].Page, clippings[3].LocationStart, clippings[3].LocationEnd));
        Assert.Equal(new DateTime(2025, 10, 6, 10, 0, 0), clippings[3].AddedAt!.Value.DateTime);
    }

    [Fact]
    public async Task Kindle_highlights_land_in_chapters_or_as_quotes_and_only_once()
    {
        var client = await factory.SignUpAsync("Kindle Highlighter");
        var sea = await (await client.PostAsJsonAsync("/books", new CreateBookRequest("A Sea Story", "Herman Example", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(KoreaderHighlightsTests.Epub()), "file", "sea-story.epub" } })
        {
            Assert.True((await client.PostAsync($"/books/{sea!.Id}/ebook", content)).IsSuccessStatusCode);
        }

        var paper = await (await client.PostAsJsonAsync("/books", new CreateBookRequest("Paper Only", "Some Writer", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions);

        var first = await UploadAsync(client);
        Assert.Equal((1, 1, 0, 2), (first.Highlights, first.Quotes, first.AlreadyHere, first.Books));
        Assert.Equal(["Not Here At All"], first.NotFound);

        var mark = Assert.Single((await client.GetFromJsonAsync<HighlightResponse[]>($"/books/{sea.Id}/highlights", JsonOptions))!);
        Assert.Equal((1, "The sea was calm and the ship was slow.", "Slow ships, calm seas."), (mark.ChapterIndex, mark.Text, mark.Note));
        var quote = Assert.Single((await client.GetFromJsonAsync<BookResponse>($"/books/{paper!.Id}", JsonOptions))!.Quotes);
        Assert.Equal(("Words from a book with no file.", 12), (quote.Text, quote.Page));

        var again = await UploadAsync(client);
        Assert.Equal((0, 0, 2), (again.Highlights, again.Quotes, again.AlreadyHere));
    }

    private static async Task<KindleImport> UploadAsync(HttpClient client)
    {
        using var content = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(Clippings)), "file", "My Clippings.txt" } };
        return (await (await client.PostAsync("/books/highlights/kindle", content)).Content.ReadFromJsonAsync<KindleImport>(JsonOptions))!;
    }
}
