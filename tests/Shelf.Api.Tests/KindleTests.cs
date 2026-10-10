using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class KindleTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task An_epub_goes_to_the_readers_kindle_address_as_an_attachment()
    {
        var client = await factory.SignUpAsync("Kindle Reader");
        var book = await CreateWithFileAsync(client, "The Dispossessed", "Ursula K. Le Guin", "dispossessed.epub",
            ImportTests.Epub("The Dispossessed", "Ursula K. Le Guin", null, "en", cover: false));

        // No address yet: it says where to add one.
        var refused = await client.PostAsync($"/books/{book.Id}/kindle", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Kindle's email address on Account", await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/account/kindle", new KindleSettingsRequest("not an address"), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/account/kindle", new KindleSettingsRequest("reader_42@kindle.com"), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/books/{book.Id}/kindle", null)).StatusCode);

        var sent = factory.Mail.Sent.Last(message => message.To == "reader_42@kindle.com");
        Assert.Equal("The Dispossessed", sent.Subject);
        var attachment = Assert.Single(sent.Attachments!);
        Assert.Equal(("Ursula K. Le Guin - The Dispossessed.epub", "application/epub+zip"), (attachment.FileName, attachment.ContentType));
        Assert.True(File.Exists(attachment.Path));

        // A comic is not something Amazon takes by email.
        var comic = await CreateWithFileAsync(client, "A Comic", "Someone", "comic.cbz", ImportTests.Comic());
        var notTaken = await client.PostAsync($"/books/{comic.Id}/kindle", null);
        Assert.Contains("EPUB or a PDF", await notTaken.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_borrowed_book_is_not_sent()
    {
        var owner = await factory.SignUpAsync("Kindle Lender");
        var borrower = await factory.SignUpAsync("Kindle Borrower");
        var borrowerId = await factory.ReaderIdAsync("Kindle Borrower");
        await borrower.PutAsJsonAsync("/account/kindle", new KindleSettingsRequest("borrower@kindle.com"), JsonOptions);
        var book = await CreateWithFileAsync(owner, "Lent Away", "Someone", "lent.epub", ImportTests.Epub("Lent Away", "Someone", null, "en", cover: false));
        await owner.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(borrowerId, null), JsonOptions);

        var refused = await borrower.PostAsync($"/books/{book.Id}/kindle", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("book of your own", await refused.Content.ReadAsStringAsync());
        Assert.DoesNotContain(factory.Mail.Sent, message => message.To == "borrower@kindle.com");
    }

    private static async Task<BookResponse> CreateWithFileAsync(HttpClient client, string title, string author, string fileName, byte[] bytes)
    {
        var book = await (await client.PostAsJsonAsync("/books", new CreateBookRequest(title, author, BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        using var content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", fileName } };
        Assert.True((await client.PostAsync($"/books/{book!.Id}/ebook", content)).IsSuccessStatusCode);
        return book;
    }
}
