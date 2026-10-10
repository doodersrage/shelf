using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class FreeBooksTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task Public_domain_books_are_found_and_added_with_their_files()
    {
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(FreeCatalog.ClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeCatalogs())));
        var client = app.CreateClient();
        await ShelfApiFactory.PostFormAsync(client, "/signup", "/account/signup", new() { ["name"] = "Free Reader", ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
        var readerId = await factory.ReaderIdAsync("Free Reader");

        var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(FreeCatalog.ClientName);
        var gutenberg = await FreeCatalog.SearchGutenbergAsync(http, "moby dick", CancellationToken.None);
        var moby = Assert.Single(gutenberg);
        Assert.Equal(("2701", "Moby Dick; Or, The Whale", "Herman Melville"), (moby.Id, moby.Title, moby.Author));
        Assert.Equal("https://www.gutenberg.org/cache/epub/2701/pg2701.cover.small.jpg", moby.CoverUrl);
        var librivox = Assert.Single(await FreeCatalog.SearchLibriVoxAsync(http, "The Moby Dick", CancellationToken.None));
        Assert.Equal(("753", "Moby Dick, or the Whale", "Herman Melville", "English"), (librivox.Id, librivox.Title, librivox.Author, librivox.Language));

        // The page searches too.
        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/free?q=moby+dick"));
        Assert.Contains("Moby Dick; Or, The Whale", page);
        Assert.Contains("Add Moby Dick; Or, The Whale to your shelf", page);
        Assert.Contains("24:37:50 long", WebUtility.HtmlDecode(await client.GetStringAsync("/free?q=moby+dick&kind=audio")));

        var downloads = app.Services.GetRequiredService<FreeBooks>();
        var ebook = await FinishAsync(downloads, downloads.Enqueue(readerId, moby), readerId);
        var audio = await FinishAsync(downloads, downloads.Enqueue(readerId, librivox), readerId);
        var missing = await FinishAsync(downloads, downloads.Enqueue(readerId, moby with { Id = "9999", Title = "Not There" }), readerId);
        var again = await FinishAsync(downloads, downloads.Enqueue(readerId, moby), readerId);

        Assert.Equal(DownloadState.Done, ebook.State);
        var book = await client.GetFromJsonAsync<BookResponse>($"/books/{ebook.BookId}", JsonOptions);
        Assert.Equal(("Moby Dick; Or, The Whale", "Herman Melville", BookFormat.Ebook), (book!.Title, book.Author, book.Format));
        Assert.Null(book.Year);
        Assert.Contains("public domain", book.Tags);
        Assert.Contains("Project Gutenberg", book.Notes);
        Assert.NotNull(book.EbookFileName);

        Assert.Equal(DownloadState.Done, audio.State);
        var recording = await client.GetFromJsonAsync<BookResponse>($"/books/{audio.BookId}", JsonOptions);
        Assert.Equal(("Moby Dick, or the Whale", BookFormat.Audiobook, "Moby Dick, or the Whale.zip"), (recording!.Title, recording.Format, recording.AudioFileName));
        Assert.Equal(2, (await client.GetStringAsync($"/library/{audio.BookId}/listen")).Split("mobydick_00").Length - 1);
        Assert.Contains("LibriVox", recording.Notes);

        Assert.Equal(DownloadState.Failed, missing.State);
        Assert.Equal("That book has no file to download.", missing.Problem);
        Assert.Equal(DownloadState.Failed, again.State);
        Assert.StartsWith("That file is already on", again.Problem);
        Assert.Equal(4, downloads.For(readerId).Count);
        Assert.Empty(downloads.For(readerId + 1000));
    }

    private static async Task<FreeDownload> FinishAsync(FreeBooks downloads, FreeDownload started, int readerId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (downloads.Find(started.Id, readerId) is { State: DownloadState.Done or DownloadState.Failed } done)
            {
                return done;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"{started.Book.Title} did not finish.");
    }

    // Gutenberg's OPDS search and EPUBs, LibriVox's API, and archive.org's zips, as they answer.
    private sealed class FakeCatalogs : HttpMessageHandler
    {
        // The same file each time, as Gutenberg would send it.
        private static readonly byte[] Moby = ImportTests.Epub("Moby Dick; Or, The Whale", "Herman Melville", null, "en", cover: true, year: "2001-07-01");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            HttpResponseMessage Text(string body, string type) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
            HttpResponseMessage Bytes(byte[] body, string type) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new(type) } } };
            var response = url switch
            {
                _ when url.StartsWith("https://www.gutenberg.org/ebooks/search.opds/", StringComparison.Ordinal) => Text("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <feed xmlns="http://www.w3.org/2005/Atom">
                      <title>Books: moby dick</title>
                      <entry><id>https://www.gutenberg.org/ebooks/search.opds/?sort_order=title</id><title>Sort Alphabetically by Title</title></entry>
                      <entry><id>https://www.gutenberg.org/ebooks/2701.opds</id><title>Moby Dick; Or, The Whale</title><content type="text">Herman Melville</content></entry>
                    </feed>
                    """, "application/atom+xml"),
                "https://www.gutenberg.org/ebooks/2701.epub3.images" => Bytes(Moby, "application/epub+zip"),
                _ when url.StartsWith("https://librivox.org/api/feed/audiobooks/?title=^moby", StringComparison.OrdinalIgnoreCase) => Text("""
                    {"books":[{"id":"753","title":"Moby Dick, or the Whale","language":"English","totaltime":"24:37:50",
                      "url_zip_file":"https://archive.org/compress/moby_dick_librivox/formats=64KBPS MP3&file=/moby_dick_librivox.zip",
                      "authors":[{"first_name":"Herman","last_name":"Melville"}]}]}
                    """, "application/json"),
                _ when url.StartsWith("https://librivox.org/api/feed/audiobooks/?id=753", StringComparison.Ordinal) => Text("""
                    {"books":[{"url_zip_file":"https://archive.org/compress/moby_dick_librivox/formats=64KBPS MP3&file=/moby_dick_librivox.zip"}]}
                    """, "application/json"),
                _ when url.StartsWith("https://archive.org/compress/moby_dick_librivox/", StringComparison.Ordinal) => Bytes(Recording(), "application/zip"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }

        private static byte[] Recording()
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in new[] { "mobydick_001_melville_64kb.mp3", "mobydick_002_melville_64kb.mp3" })
                {
                    using var stream = zip.CreateEntry(name).Open();
                    stream.Write(Encoding.ASCII.GetBytes($"audio of {name}"));
                }
            }

            return memory.ToArray();
        }
    }
}
