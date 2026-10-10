using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;

namespace Shelf.Api.Tests;

public sealed class KosyncTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task KOReader_and_Shelf_trade_places_for_the_same_file()
    {
        var client = factory.Client;
        var book = await (await client.PostAsJsonAsync("/books", new CreateBookRequest("Four Chapters", "Someone", BookStatus.Reading, null), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        var epub = FourChapters();
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(epub), "file", "four.epub" } })
        {
            await client.PostAsync($"/books/{book!.Id}/ebook", content);
        }

        // KOReader names a document by an MD5 of samples through it.
        var path = Path.Combine(Path.GetTempPath(), $"four-{Guid.NewGuid():N}.epub");
        await File.WriteAllBytesAsync(path, epub);
        var document = Kosync.Digest(path)!;
        File.Delete(path);

        var password = Kosync.NewPassword();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var reader = await db.Readers.FirstAsync(item => item.Id == factory.ReaderId);
            reader.KosyncHash = Kosync.HashOf(password);
            await db.SaveChangesAsync();
            Assert.Equal(document, (await db.Books.IgnoreQueryFilters().FirstAsync(item => item.Id == book.Id)).KoreaderDigest);
        }

        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Accept.ParseAdd(Kosync.AcceptType);
        device.DefaultRequestHeaders.Add("x-auth-user", "tenar");
        device.DefaultRequestHeaders.Add("x-auth-key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/kosync/users/auth")).StatusCode);
        device.DefaultRequestHeaders.Remove("x-auth-key");
        device.DefaultRequestHeaders.Add("x-auth-key", Md5(password));
        Assert.Equal("OK", (await device.GetFromJsonAsync<JsonElement>("/kosync/users/auth")).GetProperty("authorized").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await device.PostAsJsonAsync("/kosync/users/create", new { username = "x", password = "y" })).StatusCode);

        // Nothing read yet.
        Assert.Empty((await device.GetFromJsonAsync<JsonElement>($"/kosync/syncs/progress/{document}")).EnumerateObject());

        // The device reads into the second file of the spine; Shelf's place follows.
        const string xpointer = "/body/DocFragment[2]/body/div/p[3]/text().14";
        var put = await device.PutAsJsonAsync("/kosync/syncs/progress",
            new { document, progress = xpointer, percentage = 0.31, device = "Kobo", device_id = "kobo-1" });
        Assert.Equal(document, (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetString());
        Assert.Equal(1, (await client.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);
        var back = await device.GetFromJsonAsync<JsonElement>($"/kosync/syncs/progress/{document}");
        Assert.Equal(xpointer, back.GetProperty("progress").GetString());
        Assert.Equal("Kobo", back.GetProperty("device").GetString());

        // Read further in Shelf, and the device is sent to the start of that chapter.
        await client.PutAsJsonAsync($"/books/{book.Id}/place", new PlaceRequest(3), JsonOptions);
        var further = await device.GetFromJsonAsync<JsonElement>($"/kosync/syncs/progress/{document}");
        Assert.Equal("/body/DocFragment[4]/body", further.GetProperty("progress").GetString());
        Assert.Equal(0.75, further.GetProperty("percentage").GetDouble());
        Assert.Equal("Shelf", further.GetProperty("device").GetString());

        // Going back on the device never pulls Shelf back; a file KOReader names by its file name matches too.
        await device.PutAsJsonAsync("/kosync/syncs/progress", new { document, progress = "/body/DocFragment[1]/body", percentage = 0.1, device = "Kobo", device_id = "kobo-1" });
        Assert.Equal(3, (await client.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);
        Assert.Equal("/body/DocFragment[4]/body", (await device.GetFromJsonAsync<JsonElement>($"/kosync/syncs/progress/{Md5("four.epub")}")).GetProperty("progress").GetString());

        // A document Shelf does not have is taken and forgotten.
        Assert.Equal(HttpStatusCode.OK, (await device.PutAsJsonAsync("/kosync/syncs/progress", new { document = "0123456789abcdef0123456789abcdef", progress = "12", percentage = 0.5, device = "Kobo", device_id = "kobo-1" })).StatusCode);
        Assert.Empty((await device.GetFromJsonAsync<JsonElement>("/kosync/syncs/progress/0123456789abcdef0123456789abcdef")).EnumerateObject());
    }

    [Fact]
    public void A_place_is_read_from_an_xpointer_a_page_or_a_share()
    {
        Assert.Equal(11, Kosync.ChapterOf("/body/DocFragment[12]/body/p[3]/text().0", 0.5, 30));
        Assert.Equal(44, Kosync.ChapterOf("45", 0.2, 300));
        Assert.Equal(15, Kosync.ChapterOf("#_doc_fragment_something", 0.5, 30));
        Assert.Equal(0, Kosync.ChapterOf("nonsense", null, 30));
    }

    private static string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static byte[] FourChapters()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }

            Write("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
            var items = string.Concat(Enumerable.Range(1, 4).Select(n => $"<item id=\"c{n}\" href=\"c{n}.xhtml\" media-type=\"application/xhtml+xml\"/>"));
            var spine = string.Concat(Enumerable.Range(1, 4).Select(n => $"<itemref idref=\"c{n}\"/>"));
            Write("content.opf", $"""<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Four Chapters</dc:title></metadata><manifest>{items}</manifest><spine>{spine}</spine></package>""");
            for (var n = 1; n <= 4; n++)
            {
                Write($"c{n}.xhtml", $"""<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter {n}</title></head><body><p>{new string('x', 3000)} {n}</p></body></html>""");
            }
        }

        return memory.ToArray();
    }
}
