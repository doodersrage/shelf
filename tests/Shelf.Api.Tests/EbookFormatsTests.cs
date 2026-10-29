using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class EbookFormatsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // A Kindle book, as far as Shelf looks: BOOKMOBI sixty bytes in.
    private static byte[] Kindle(string words)
    {
        var bytes = new byte[200];
        "BOOKMOBI"u8.CopyTo(bytes.AsSpan(60));
        System.Text.Encoding.UTF8.GetBytes(words).CopyTo(bytes.AsSpan(100));
        return bytes;
    }

    private static byte[] Pdf(string words) => System.Text.Encoding.ASCII.GetBytes($"%PDF-1.4\n% {words}\n%%EOF\n");

    private static async Task<BookResponse> BookWithEpubAsync(HttpClient client, string title)
    {
        var book = (await (await client.PostAsJsonAsync("/books", new CreateBookRequest(title, "Ann Writer", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using var content = new MultipartFormDataContent { { new ByteArrayContent(BooksEndpointTests.SampleEpub($"The main e-book of {title}.")), "file", "main.epub" } };
        Assert.EndsWith("?ebook=saved", (await client.PostAsync($"/books/{book.Id}/ebook", content)).RequestMessage!.RequestUri!.PathAndQuery);
        return book;
    }

    private static async Task<string> AddAsync(HttpClient client, int bookId, byte[] bytes, string name)
    {
        using var content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
        var query = (await client.PostAsync($"/books/{bookId}/ebook/formats", content)).RequestMessage!.RequestUri!.Query;
        return query.Split("format=")[1];
    }

    // Another format is kept as it came, one of each to a book; a file that isn't what its name says is turned away,
    // as is the main e-book's own format. It downloads named for the book, and no one else reaches it.
    [Fact]
    public async Task Another_format_is_kept_beside_the_main_ebook_and_downloads_as_it_came()
    {
        var reader = await factory.SignUpAsync("Format Keeper");
        var book = await BookWithEpubAsync(reader, "Two Formats");
        var azw3 = Kindle("first copy");
        Assert.Equal("saved", await AddAsync(reader, book.Id, azw3, "two.azw3"));
        Assert.Equal("damaged", await AddAsync(reader, book.Id, "not a kindle book"u8.ToArray(), "fake.azw3"));
        Assert.Equal("main", await AddAsync(reader, book.Id, BooksEndpointTests.SampleEpub("Another EPUB."), "again.epub"));
        Assert.Equal("unsupported", await AddAsync(reader, book.Id, [1, 2, 3], "notes.txt"));

        var formats = (await reader.GetFromJsonAsync<EbookFormatResponse[]>($"/books/{book.Id}/ebook/formats", JsonOptions))!;
        var kept = Assert.Single(formats);
        Assert.Equal(("AZW3", "two.azw3", false), (kept.Format, kept.FileName, kept.CanBeMain));
        var download = await reader.GetAsync($"/books/{book.Id}/ebook/formats/{kept.Id}");
        Assert.Equal(azw3, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("Ann Writer - Two Formats.azw3", download.Content.Headers.ContentDisposition!.FileNameStar ?? download.Content.Headers.ContentDisposition.FileName!.Trim('"'));

        // A second AZW3 takes the first one's place.
        var newer = Kindle("second copy");
        Assert.Equal("saved", await AddAsync(reader, book.Id, newer, "newer.azw3"));
        kept = Assert.Single((await reader.GetFromJsonAsync<EbookFormatResponse[]>($"/books/{book.Id}/ebook/formats", JsonOptions))!);
        Assert.Equal(newer, await reader.GetByteArrayAsync($"/books/{book.Id}/ebook/formats/{kept.Id}"));

        // The catalog an e-reader app browses offers it beside the main e-book.
        string key;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<Shelf.Api.Readers.ShelfReader>().Use(await factory.ReaderIdAsync("Format Keeper"));
            key = await Shelf.Api.Readers.ReaderRules.NewKeyAsync(scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>(), await factory.ReaderIdAsync("Format Keeper"));
        }

        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"kobo:{key}")));
        var feed = await device.GetStringAsync("/opds/books");
        Assert.Contains($"/opds/books/{book.Id}/formats/{kept.Id}", feed);
        Assert.Equal(newer, await device.GetByteArrayAsync($"/opds/books/{book.Id}/formats/{kept.Id}"));

        var stranger = await factory.SignUpAsync("Format Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/ebook/formats")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/ebook/formats/{kept.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/books/{book.Id}/ebook/formats/{kept.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/{book.Id}/ebook/formats/{kept.Id}")).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<EbookFormatResponse[]>($"/books/{book.Id}/ebook/formats", JsonOptions))!);
    }

    // A format the reader opens becomes the main e-book, and the main one before is kept as another format; the
    // place starts again, as a place in one file means nothing in another. A Kindle book can't be the main one.
    [Fact]
    public async Task A_format_the_reader_opens_can_be_made_the_main_ebook()
    {
        var reader = await factory.SignUpAsync("Format Switcher");
        var book = await BookWithEpubAsync(reader, "Switched Formats");
        await reader.PutAsJsonAsync($"/books/{book.Id}/place", new PlaceRequest(3), JsonOptions);
        Assert.Equal("saved", await AddAsync(reader, book.Id, Pdf("printed pages"), "printed.pdf"));
        Assert.Equal("saved", await AddAsync(reader, book.Id, Kindle("kindle"), "kindle.mobi"));
        var formats = (await reader.GetFromJsonAsync<EbookFormatResponse[]>($"/books/{book.Id}/ebook/formats", JsonOptions))!;

        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsync($"/books/{book.Id}/ebook/formats/{formats.Single(format => format.Format == "MOBI").Id}/main", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync($"/books/{book.Id}/ebook/formats/{formats.Single(format => format.Format == "PDF").Id}/main", null)).StatusCode);
        var now = (await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!;
        Assert.Equal("printed.pdf", now.EbookFileName);
        Assert.Equal("application/pdf", (await reader.GetAsync($"/books/{book.Id}/ebook/file")).Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, (await reader.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);
        Assert.Equal(["EPUB", "MOBI"], (await reader.GetFromJsonAsync<EbookFormatResponse[]>($"/books/{book.Id}/ebook/formats", JsonOptions))!.Select(format => format.Format));
    }

    // Deleted with its book, a format comes back with it from the trash; it goes out in the full export and comes
    // back in on another shelf; and a merge keeps the other entry's main e-book as a format, given back on unmerge.
    [Fact]
    public async Task Formats_survive_the_trash_the_export_and_a_merge()
    {
        var reader = await factory.SignUpAsync("Format Survivor");
        var book = await BookWithEpubAsync(reader, "Surviving Formats");
        var pdf = Pdf("survives");
        Assert.Equal("saved", await AddAsync(reader, book.Id, pdf, "survives.pdf"));

        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/{book.Id}")).StatusCode);
        var trashed = Assert.Single((await reader.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions))!, item => item.BookId == book.Id);
        var restore = (await (await reader.PostAsync($"/books/trash/{trashed.Id}/restore", null)).Content.ReadFromJsonAsync<TrashRestore>(JsonOptions))!;
        Assert.True(restore.Restored);
        var back = Assert.Single((await reader.GetFromJsonAsync<EbookFormatResponse[]>($"/books/{restore.BookId}/ebook/formats", JsonOptions))!);
        Assert.Equal(pdf, await reader.GetByteArrayAsync($"/books/{restore.BookId}/ebook/formats/{back.Id}"));

        var zip = await reader.GetByteArrayAsync("/books/export/full");
        using (var archive = new ZipArchive(new MemoryStream(zip)))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "books/0001/formats/pdf/survives.pdf");
        }

        var elsewhere = await factory.SignUpAsync("Format Restorer");
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(zip), "file", "backup.zip" } })
        {
            Assert.Contains("files=2", (await elsewhere.PostAsync("/books/import/full", content)).RequestMessage!.RequestUri!.Query);
        }

        var restored = Assert.Single((await elsewhere.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!);
        Assert.Equal("PDF", Assert.Single((await elsewhere.GetFromJsonAsync<EbookFormatResponse[]>($"/books/{restored.Id}/ebook/formats", JsonOptions))!).Format);

        // Two entries for the book, one an EPUB, one a Kindle book: merged, the Kindle book stays as a format.
        var epubEntry = await BookWithEpubAsync(reader, "Merged Formats");
        var kindleEntry = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Merged Formats (Kindle)", "Ann Writer", BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(Pdf("the other entry")), "file", "other.pdf" } })
        {
            await reader.PostAsync($"/books/{kindleEntry.Id}/ebook", content);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync($"/books/{epubEntry.Id}/merge", new MergeRequest(kindleEntry.Id), JsonOptions)).StatusCode);
        Assert.Equal("other.pdf", Assert.Single((await reader.GetFromJsonAsync<EbookFormatResponse[]>($"/books/{epubEntry.Id}/ebook/formats", JsonOptions))!).FileName);

        var merged = Assert.Single((await reader.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions))!, item => item.BookId == kindleEntry.Id);
        var unmerged = (await (await reader.PostAsync($"/books/trash/{merged.Id}/restore", null)).Content.ReadFromJsonAsync<TrashRestore>(JsonOptions))!;
        Assert.True(unmerged.Restored);
        Assert.Equal("other.pdf", (await reader.GetFromJsonAsync<BookResponse>($"/books/{unmerged.BookId}", JsonOptions))!.EbookFileName);
        Assert.Empty((await reader.GetFromJsonAsync<EbookFormatResponse[]>($"/books/{epubEntry.Id}/ebook/formats", JsonOptions))!);
    }
}
