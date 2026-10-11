using Microsoft.Extensions.Configuration;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class ComicTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures"));

    [Fact]
    public async Task A_comic_in_a_tar_archive_is_repacked_as_a_cbz_with_its_pages_in_order()
    {
        var reader = await factory.SignUpAsync("Comic Packer");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Packed", "Someone", BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using var tar = new MemoryStream();
        using (var writer = SharpCompress.Writers.WriterFactory.OpenWriter(tar, SharpCompress.Common.ArchiveType.Tar, new SharpCompress.Writers.WriterOptions(SharpCompress.Common.CompressionType.None) { LeaveStreamOpen = true }))
        {
            using var comic = ZipFile.OpenRead(Path.Combine(Fixtures, "comic.cbz"));
            foreach (var entry in comic.Entries.Where(entry => entry.Length > 0 && !entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Reverse())
            {
                using var page = new MemoryStream();
                using (var input = entry.Open())
                {
                    input.CopyTo(page);
                }

                page.Position = 0;
                writer.Write(entry.FullName, page, DateTime.UtcNow);
            }

            writer.Write("ComicInfo.xml", new MemoryStream("<ComicInfo><Manga>YesAndRightToLeft</Manga></ComicInfo>"u8.ToArray()), DateTime.UtcNow);
        }

        using var content = new MultipartFormDataContent { { new ByteArrayContent(tar.ToArray()), "file", "packed.cbt" } };
        (await reader.PostAsync($"/books/{book.Id}/ebook", content)).EnsureSuccessStatusCode();
        var after = await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions);
        Assert.Equal("packed.cbz", after!.EbookFileName);

        int pages;
        using (var original = ZipFile.OpenRead(Path.Combine(Fixtures, "comic.cbz")))
        {
            pages = original.Entries.Count(entry => entry.Length > 0 && !entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/books/{book.Id}/ebook/pages/{pages - 1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/books/{book.Id}/ebook/pages/{pages}")).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<EbookStore>();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var stored = db.Books.IgnoreQueryFiltersFirst(book.Id);
        Assert.True(ComicFile.IsRightToLeft(store.OpenPath(stored)));
    }

    [Fact]
    public void A_spread_shows_the_cover_alone_then_two_pages_at_a_time()
    {
        Assert.Equal((0, 1), ComicFile.Spread(0, 6, true));
        Assert.Equal((1, 2), ComicFile.Spread(1, 6, true));
        Assert.Equal((1, 2), ComicFile.Spread(2, 6, true));
        Assert.Equal((5, 1), ComicFile.Spread(5, 6, true));
        Assert.Equal((4, 1), ComicFile.Spread(4, 6, false));
    }

    [Fact]
    public async Task A_comicvine_issue_gives_the_series_number_writers_publisher_and_year()
    {
        var reader = await factory.SignUpAsync("Comic Matcher");
        var readerId = await factory.ReaderIdAsync("Comic Matcher");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("saga 12", "Unknown", BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        var handler = new Answers(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.Contains("/search/", StringComparison.Ordinal) => """{"results":[{"id":101,"name":"Chapter Twelve","issue_number":"12","cover_date":"2013-05-01","volume":{"id":55,"name":"Saga"},"image":{"original_url":"https://comicvine.gamespot.com/a/saga12.jpg"}}]}""",
            var path when path.Contains("/issue/", StringComparison.Ordinal) => """{"results":{"id":101,"name":"Chapter Twelve","issue_number":"12","cover_date":"2013-05-01","volume":{"id":55,"name":"Saga"},"person_credits":[{"name":"Brian K. Vaughan","role":"writer"},{"name":"Fiona Staples","role":"artist, cover"}]}}""",
            _ => """{"results":{"publisher":{"name":"Image"}}}""",
        });
        var catalog = new ComicVineCatalog(new HttpClient(handler), new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Comics:ComicVineKey"] = "key" }).Build());

        var found = Assert.Single(await catalog.SearchAsync("saga 12", CancellationToken.None));
        Assert.Equal(new ComicIssue(101, "Saga", "12", "Chapter Twelve", 2013, "https://comicvine.gamespot.com/a/saga12.jpg", 55), found);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            Assert.True(await ComicVine.ApplyAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), catalog, scope.ServiceProvider.GetRequiredService<CoverStore>(), book.Id, 101, cover: false));
        }

        var after = await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions);
        Assert.Equal(("Saga", (int?)12, "Brian K. Vaughan", "Image", (int?)2013), (after!.Series, after.SeriesNumber, after.Author, after.Publisher, after.Year));
        Assert.Null(await catalog.ImageAsync("https://example.com/cover.jpg", CancellationToken.None));
    }

    private sealed class Answers(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request), Encoding.UTF8, "application/json") });
    }
}

internal static class BookStoredName
{
    public static string? IgnoreQueryFiltersFirst(this Microsoft.EntityFrameworkCore.DbSet<Book> books, int id) =>
        Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(books).Where(book => book.Id == id).Select(book => book.EbookStoredName).First();
}
