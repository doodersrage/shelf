using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class EpubDetailsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    private static readonly EpubDetailsToWrite Details = new("The Shelf Title", ["Ann Writer", "Bo Second"], "The Long Series", 3, "Good Press", "fr", 1999, "978-0-306-40615-7", null);

    // EPUB 3: the shelf's details replace the file's, authors with their role, the series both ways, and the book's
    // own identifier kept, taking the ISBN; what the shelf doesn't know (the rights) stays as the file had it.
    [Fact]
    public void An_epub_3_package_takes_the_shelfs_details()
    {
        var package = XDocument.Parse("""
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="uid">urn:isbn:9781111111111</dc:identifier>
                <dc:title id="t1">Old Title</dc:title>
                <dc:title id="t2">An Old Subtitle</dc:title>
                <meta refines="#t2" property="title-type">subtitle</meta>
                <dc:creator id="c1">Wrong Name</dc:creator>
                <meta refines="#c1" property="role" scheme="marc:relators">aut</meta>
                <dc:language>en</dc:language>
                <dc:rights>Public domain</dc:rights>
                <meta property="belongs-to-collection" id="s">Old Series</meta>
                <meta refines="#s" property="group-position">9</meta>
              </metadata>
              <manifest><item id="old-cover" href="cover.jpg" media-type="image/jpeg" properties="cover-image"/></manifest>
            </package>
            """);

        Assert.True(EpubDetails.Apply(package, Details, ("shelf-cover.png", "image/png")));
        var metadata = package.Root!.Element(Opf + "metadata")!;
        Assert.Equal("The Shelf Title", Assert.Single(metadata.Elements(Dc + "title")).Value);
        Assert.Equal(["Ann Writer", "Bo Second"], metadata.Elements(Dc + "creator").Select(creator => creator.Value));
        Assert.Equal(2, metadata.Elements(Opf + "meta").Count(meta => (string?)meta.Attribute("property") == "role"));
        Assert.DoesNotContain(metadata.Elements(Opf + "meta"), meta => (string?)meta.Attribute("refines") is "#t2" or "#c1" or "#s");
        Assert.Equal("fr", Assert.Single(metadata.Elements(Dc + "language")).Value);
        Assert.Equal("Good Press", metadata.Element(Dc + "publisher")!.Value);
        Assert.Equal("1999", metadata.Element(Dc + "date")!.Value);
        Assert.Equal("urn:isbn:9780306406157", Assert.Single(metadata.Elements(Dc + "identifier")).Value);
        Assert.Equal("Public domain", metadata.Element(Dc + "rights")!.Value);
        Assert.Equal("The Long Series", Meta(metadata, "calibre:series"));
        Assert.Equal("3", Meta(metadata, "calibre:series_index"));
        Assert.Equal("The Long Series", Assert.Single(metadata.Elements(Opf + "meta"), meta => (string?)meta.Attribute("property") == "belongs-to-collection").Value);
        Assert.Equal("3", metadata.Elements(Opf + "meta").Single(meta => (string?)meta.Attribute("property") == "group-position").Value);

        // The shelf's cover is the cover now; the file's own picture stays, no longer called the cover.
        var items = package.Root.Element(Opf + "manifest")!.Elements(Opf + "item").ToList();
        Assert.Null(items.Single(item => (string?)item.Attribute("id") == "old-cover").Attribute("properties"));
        Assert.Equal("cover-image", (string?)items.Single(item => (string?)item.Attribute("id") == "shelf-cover").Attribute("properties"));
        Assert.Equal("shelf-cover", Meta(metadata, "cover"));
    }

    // EPUB 2 keeps its roles and schemes as attributes, and its other dates (a modification, say) are left alone.
    [Fact]
    public void An_epub_2_package_takes_them_its_own_way_and_blank_details_leave_the_files()
    {
        var package = XDocument.Parse("""
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="uid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">
                <dc:identifier id="uid">calibre-1234</dc:identifier>
                <dc:identifier opf:scheme="ISBN">1111111111</dc:identifier>
                <dc:title>Old</dc:title>
                <dc:publisher>Kept Press</dc:publisher>
                <dc:date opf:event="modification">2020-01-01</dc:date>
                <dc:date>1850</dc:date>
              </metadata>
              <manifest/>
            </package>
            """);

        Assert.True(EpubDetails.Apply(package, Details with { Publisher = null, Series = null }));
        var metadata = package.Root!.Element(Opf + "metadata")!;
        Assert.All(metadata.Elements(Dc + "creator"), creator => Assert.Equal("aut", (string?)creator.Attribute(Opf + "role")));
        Assert.Equal(["calibre-1234", "9780306406157"], metadata.Elements(Dc + "identifier").Select(identifier => identifier.Value));
        Assert.Equal("ISBN", (string?)metadata.Elements(Dc + "identifier").Last().Attribute(Opf + "scheme"));
        Assert.Equal(["2020-01-01", "1999"], metadata.Elements(Dc + "date").Select(date => date.Value));
        Assert.Equal("Kept Press", metadata.Element(Dc + "publisher")!.Value);
        Assert.Null(Meta(metadata, "calibre:series"));
    }

    [Theory]
    [InlineData("English", "en")]
    [InlineData("french", "fr")]
    [InlineData("Deutsch", "de")]
    [InlineData("en-GB", "en-GB")]
    [InlineData("Elvish", null)]
    [InlineData("", null)]
    public void A_language_is_written_as_its_code(string language, string? code) =>
        Assert.Equal(code, EpubDetails.LanguageCode(language));

    // The download carries the shelf's details and cover; the file kept, the one the reader and KOReader use, is
    // unchanged. Changed details make a new copy. A stranger gets nothing.
    [Fact]
    public async Task A_downloaded_epub_carries_the_shelfs_details_and_the_stored_file_is_untouched()
    {
        var reader = await factory.SignUpAsync("Details Downloader");
        var created = await reader.PostAsJsonAsync("/books", new CreateBookRequest("Shelf's Own Title", "Ann Writer & Bo Second", BookStatus.Reading, 5,
            Notes: "A private note.", Tags: ["secret-tag"], Series: "The Long Series", SeriesNumber: 2, Language: "English", Isbn: "9780306406157"), JsonOptions);
        var book = (await created.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        var original = BooksEndpointTests.SampleEpub("Details travel with the file.");
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(original), "file", "sample.epub" } })
        {
            Assert.EndsWith("?ebook=saved", (await reader.PostAsync($"/books/{book.Id}/ebook", content)).RequestMessage!.RequestUri!.PathAndQuery);
        }

        var response = await reader.GetAsync($"/books/{book.Id}/ebook/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ann Writer & Bo Second - Shelf's Own Title.epub", response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        var package = Package(await response.Content.ReadAsByteArrayAsync(), out var text);
        var metadata = package.Root!.Element(Opf + "metadata")!;
        Assert.Equal("Shelf's Own Title", metadata.Element(Dc + "title")!.Value);
        Assert.Equal(["Ann Writer", "Bo Second"], metadata.Elements(Dc + "creator").Select(creator => creator.Value));
        Assert.Equal("en", metadata.Element(Dc + "language")!.Value);
        Assert.Equal("2", Meta(metadata, "calibre:series_index"));
        Assert.DoesNotContain("private note", text);
        Assert.DoesNotContain("secret-tag", text);

        Assert.Equal(original, await reader.GetByteArrayAsync($"/books/{book.Id}/ebook/file"));

        var update = await reader.PutAsJsonAsync($"/books/{book.Id}", new UpdateBookRequest("A Better Title", "Ann Writer & Bo Second", BookStatus.Reading, 5, Series: "The Long Series", SeriesNumber: 2), JsonOptions);
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
        var again = Package(await reader.GetByteArrayAsync($"/books/{book.Id}/ebook/download"), out _);
        Assert.Equal("A Better Title", again.Root!.Element(Opf + "metadata")!.Element(Dc + "title")!.Value);

        var stranger = await factory.SignUpAsync("Details Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/ebook/download")).StatusCode);
    }

    // A cover known only by its address goes into the download too: fetched once, then kept with the copies.
    [Fact]
    public async Task A_cover_known_by_its_address_goes_into_the_download()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var asked = 0;
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(EpubDetailsStore.CoverClient).ConfigurePrimaryHttpMessageHandler(() => new Answers(request =>
            {
                Interlocked.Increment(ref asked);
                return request.RequestUri!.Host == "covers.example"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } } }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }))));
        var reader = app.CreateClient();
        await ShelfApiFactory.PostFormAsync(reader, "/signup", "/account/signup", new() { ["name"] = "Cover Address Reader", ["email"] = ShelfApiFactory.EmailOf("Cover Address Reader"), ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
        var created = await reader.PostAsJsonAsync("/books", new CreateBookRequest("Covered By Address", "Someone", BookStatus.Reading, null, CoverUrl: "https://covers.example/heron.png"), JsonOptions);
        var book = (await created.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(BooksEndpointTests.SampleEpub($"Covered {Guid.NewGuid():N}.")), "file", "covered.epub" } })
        {
            await reader.PostAsync($"/books/{book.Id}/ebook", content);
        }

        bool Holds(byte[] epub)
        {
            using var zip = new ZipArchive(new MemoryStream(epub));
            return zip.Entries.Any(entry =>
            {
                using var stream = entry.Open();
                var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                return bytes.ToArray().SequenceEqual(png);
            });
        }

        Assert.True(Holds(await reader.GetByteArrayAsync($"/books/{book.Id}/ebook/download")));
        Assert.True(Holds(await reader.GetByteArrayAsync($"/books/{book.Id}/ebook/download")));
        Assert.Equal(1, asked);
    }

    // A book whose file the server has lost says so in the reader, with the way to put it back, not "could not open".
    [Fact]
    public async Task A_missing_file_is_called_missing_in_the_reader()
    {
        var reader = await factory.SignUpAsync("Lost File Reader");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Mislaid", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(BooksEndpointTests.SampleEpub($"Mislaid {Guid.NewGuid():N}.")), "file", "mislaid.epub" } })
        {
            await reader.PostAsync($"/books/{book.Id}/ebook", content);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
            var stored = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.Books), item => item.Id == book.Id);
            File.Delete(scope.ServiceProvider.GetRequiredService<EbookStore>().OpenPath(stored.EbookStoredName)!);
        }

        var page = await reader.GetStringAsync($"/library/{book.Id}/read");
        Assert.Contains("missing from the server", page);
        Assert.Contains($"/library/{book.Id}#files", page);
    }

    private sealed class Answers(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    private static string? Meta(XElement metadata, string name) =>
        metadata.Elements(Opf + "meta").FirstOrDefault(meta => (string?)meta.Attribute("name") == name)?.Attribute("content")?.Value;

    private static XDocument Package(byte[] epub, out string text)
    {
        using var zip = new ZipArchive(new MemoryStream(epub));
        Assert.DoesNotContain(zip.Entries.Skip(1), entry => entry.FullName == "mimetype");
        using var reader = new StreamReader(zip.GetEntry("OEBPS/content.opf")!.Open());
        text = reader.ReadToEnd();
        return XDocument.Parse(text);
    }
}
