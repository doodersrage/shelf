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

    // A chapter as real ones come: a doctype, HTML's named characters, spaces between blocks, emphasis, and a script.
    private const string Chapter = """
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.1//EN" "http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd">
        <html xmlns="http://www.w3.org/1999/xhtml">
          <head><title>One</title><script>alert(1)</script></head>
          <body>
            <h1>Chapter&nbsp;One</h1>
            <script>alert(2)</script>
            <div class="text">
              <p>First.</p>
              <p>Second <em>with</em> more words.</p>
              <p>Third paragraph here.</p>
              <p><em>Lead</em>tail words</p>
            </div>
          </body>
        </html>
        """;

    [Theory]
    // The third paragraph's words: the div is the body's second element (the script is gone, as in the reader).
    [InlineData("/body/DocFragment[2]/body/div/p[3]/text().6", "epubcfi(/6/4!/4/4/6/1:6)")]
    // Words after emphasis: the paragraph's second text node.
    [InlineData("/body/DocFragment[2]/body/div/p[2]/text()[2].1", "epubcfi(/6/4!/4/4/4/3:1)")]
    // A named character read as itself.
    [InlineData("/body/DocFragment[2]/body/h1/text().8", "epubcfi(/6/4!/4/2/1:8)")]
    // Words after an element with no text before it: epub.js counts it the first text node.
    [InlineData("/body/DocFragment[2]/body/div/p[4]/text().4", "epubcfi(/6/4!/4/4/8/1:4)")]
    public void A_KOReader_place_and_a_readers_are_one_and_the_same(string xpointer, string cfi)
    {
        var document = EpubFile.ChapterDocument(Chapter)!;
        Assert.Equal(cfi, KoreaderPlace.ToCfi(xpointer, 1, document));
        Assert.Equal(xpointer, KoreaderPlace.ToXPointer(cfi, document));
        Assert.Equal(1, KoreaderPlace.ChapterOf(xpointer));
        Assert.Equal(1, KoreaderPlace.ChapterOfCfi(cfi));
    }

    [Fact]
    public void A_place_with_epubjs_assertions_or_none_in_the_chapter_is_read_or_let_go()
    {
        var document = EpubFile.ChapterDocument(Chapter)!;
        // epub.js writes the spine item's id and an element's, in brackets.
        Assert.Equal("/body/DocFragment[2]/body/div/p[3]/text().6", KoreaderPlace.ToXPointer("epubcfi(/6/4[c2]!/4[body]/4/6/1:6)", document));
        // An element alone, and a KOReader place at an element.
        Assert.Equal("/body/DocFragment[2]/body/div/p[2]/text().0", KoreaderPlace.ToXPointer("epubcfi(/6/4!/4/4/4)", document));
        Assert.Equal("epubcfi(/6/4!/4/4/2)", KoreaderPlace.ToCfi("/body/DocFragment[2]/body/div/p", 1, document));
        // Places that lead nowhere.
        Assert.Null(KoreaderPlace.ToCfi("/body/DocFragment[2]/body/section/p[9]/text().0", 1, document));
        Assert.Null(KoreaderPlace.ToXPointer("epubcfi(/6/4!/4/40/2)", document));
        Assert.Null(KoreaderPlace.ToCfi("#_doc_fragment_3", 1, document));
        Assert.Null(EpubFile.ChapterDocument("<html><body><p>Not XML</body></html>"));

        // Which comes first, by path: a later paragraph, further words, an element's own start before what's in it.
        Assert.True(KoreaderPlace.Compare("epubcfi(/6/4!/4/4/6/1:6)", "epubcfi(/6/4[c2]!/4/4/8/1:0)") < 0);
        Assert.True(KoreaderPlace.Compare("epubcfi(/6/4!/4/4/6/1:9)", "epubcfi(/6/4!/4/4/6/1:6)") > 0);
        Assert.True(KoreaderPlace.Compare("epubcfi(/6/4!/4/4/6)", "epubcfi(/6/4!/4/4/6/1:0)") < 0);
        Assert.True(KoreaderPlace.Compare("epubcfi(/6/6!/4/2)", "epubcfi(/6/4!/4/40/1:0)") > 0);
        Assert.Equal(0, KoreaderPlace.Compare("epubcfi(/6/4!/4/4/6/1:6)", "epubcfi(/6/4!/4/4/6/1:6)"));
        Assert.Null(KoreaderPlace.Compare("nonsense", "epubcfi(/6/4!/4)"));
    }

    // Read again, the book starts over on the devices too: KOReader saying the end of the last time through again
    // doesn't jump the reread there, nor is it handed back as the place to go; somewhere new it says still counts;
    // the Kobo is told the book is being read again; and the last read's device places go.
    [Fact]
    public async Task A_book_read_again_starts_over_on_koreader_and_the_kobo()
    {
        var reader = await factory.SignUpAsync("Rereader");
        var readerId = await factory.ReaderIdAsync("Rereader");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Four Chapters Again", "Someone", BookStatus.Finished, null), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        var epub = FourChapters();
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(epub), "file", "four.epub" } })
        {
            await reader.PostAsync($"/books/{book.Id}/ebook", content);
        }

        var path = Path.Combine(Path.GetTempPath(), $"four-{Guid.NewGuid():N}.epub");
        await File.WriteAllBytesAsync(path, epub);
        var document = Kosync.Digest(path)!;
        File.Delete(path);
        var password = Kosync.NewPassword();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            (await db.Readers.FirstAsync(item => item.Id == readerId)).KosyncHash = Kosync.HashOf(password);
            await db.SaveChangesAsync();
        }

        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Accept.ParseAdd(Kosync.AcceptType);
        device.DefaultRequestHeaders.Add("x-auth-user", "rereader");
        device.DefaultRequestHeaders.Add("x-auth-key", Md5(password));

        // The first time through ends in the last chapter, on KOReader and on a Kobo.
        const string end = "/body/DocFragment[4]/body/p[1]/text().0";
        await device.PutAsJsonAsync("/kosync/syncs/progress", new { document, progress = end, percentage = 0.97, device = "Boox", device_id = "boox-1" });
        Assert.Equal(3, (await reader.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            db.KoboEntries.Add(new KoboEntry { ReaderId = readerId, BookId = book.Id, Status = "Finished", Bookmark = "{\"ProgressPercent\":100}", SyncedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync($"/books/{book.Id}/again", null)).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<PositionsOfBook>($"/books/{book.Id}/positions", JsonOptions))!.Reports);
        using (var scope = factory.Services.CreateScope())
        {
            var entry = await scope.ServiceProvider.GetRequiredService<ShelfDb>().KoboEntries.IgnoreQueryFilters().SingleAsync(item => item.ReaderId == readerId && item.BookId == book.Id);
            Assert.Equal(((string?)null, (string?)null), (entry.Status, entry.Bookmark));
        }

        // KOReader is sent to the start, not the old end; and saying the old end again moves nothing.
        var back = await device.GetFromJsonAsync<JsonElement>($"/kosync/syncs/progress/{document}");
        Assert.Equal("/body/DocFragment[1]/body", back.GetProperty("progress").GetString());
        await device.PutAsJsonAsync("/kosync/syncs/progress", new { document, progress = end, percentage = 0.97, device = "Boox", device_id = "boox-1" });
        Assert.Null((await reader.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);

        // Reading on in the reread counts as ever.
        await device.PutAsJsonAsync("/kosync/syncs/progress", new { document, progress = "/body/DocFragment[2]/body/p[1]/text().0", percentage = 0.3, device = "Boox", device_id = "boox-1" });
        Assert.Equal(1, (await reader.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);
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
