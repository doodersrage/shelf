using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

// With a real Calibre: SHELF_TEST_CALIBRE names the folder holding ebook-convert, ebook-meta, and calibredb. Without
// it these say nothing and pass, as the PostgreSQL tests do without their server.
public sealed class RealCalibreTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string? Calibre = Environment.GetEnvironmentVariable("SHELF_TEST_CALIBRE");

    private static string Fixture(string name)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "tests", "e2e", "fixtures")))
        {
            folder = folder.Parent;
        }

        return Path.Combine(folder!.FullName, "tests", "e2e", "fixtures", name);
    }

    private static async Task<string> RunAsync(string tool, params string[] arguments) =>
        await Tools.RunAsync(Path.Combine(Calibre!, tool), arguments, TimeSpan.FromMinutes(5), CancellationToken.None)
        ?? throw new InvalidOperationException($"{tool} failed");

    // A real conversion to each format Shelf offers, kept as another format; a Kindle file made that way turned back
    // into an EPUB as it comes in; the details Shelf writes into an EPUB read back by Calibre; and the folder export
    // added to a Calibre library, its details whole.
    [Fact]
    public async Task Calibre_converts_reads_the_details_and_takes_the_folder_export()
    {
        if (string.IsNullOrEmpty(Calibre))
        {
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), $"shelf-calibre-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var reader = await factory.SignUpAsync("Calibre Tester");
            var created = await reader.PostAsJsonAsync("/books", new CreateBookRequest("Moby-Dick; or, The Whale", "Herman Melville", BookStatus.Reading, 4,
                Year: 1851, Isbn: "9780142437247", Language: "English", Series: "American Classics", SeriesNumber: 3, Tags: ["sea", "whales"], Publisher: "Harper"), JsonOptions);
            var book = (await created.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
            using (var content = new MultipartFormDataContent { { new ByteArrayContent(await File.ReadAllBytesAsync(Fixture("moby.epub"))), "file", "moby.epub" } })
            {
                Assert.EndsWith("?ebook=saved", (await reader.PostAsync($"/books/{book.Id}/ebook", content)).RequestMessage!.RequestUri!.PathAndQuery);
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Calibre Tester"));
                var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
                var shared = scope.ServiceProvider.GetRequiredService<EbookStore>();
                var store = new EbookStore(scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(),
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["EbookStore:Root"] = shared.Root, ["Ebooks:Convert"] = Path.Combine(Calibre, "ebook-convert") }).Build());
                var storage = scope.ServiceProvider.GetRequiredService<Storage>();
                var entry = await db.Books.FirstAsync(item => item.Id == book.Id);
                foreach (var format in new[] { "azw3", "mobi", "pdf" })
                {
                    Assert.Equal("saved", await EbookFormats.ConvertAsync(db, store, storage, entry, format));
                }

                // A Kindle file comes in as an EPUB, made by Calibre.
                var mobi = await db.EbookFormats.AsNoTracking().SingleAsync(item => item.BookId == book.Id && item.StoredName.EndsWith(".mobi"));
                await using var kindle = File.OpenRead(shared.OpenPath(mobi.StoredName)!);
                var cameIn = await store.SaveAsync(kindle, "whale.mobi", CancellationToken.None);
                Assert.Equal(EbookSaveStatus.Saved, cameIn.Status);
                Assert.True(EpubFile.Chapters(shared.OpenPath(cameIn.StoredName)!)?.Count > 0);
                shared.Delete(cameIn.StoredName);
            }

            var formats = (await reader.GetFromJsonAsync<EbookFormatResponse[]>($"/books/{book.Id}/ebook/formats", JsonOptions))!;
            Assert.Equal(["AZW3", "MOBI", "PDF"], formats.Select(format => format.Format));
            var pdf = await reader.GetByteArrayAsync($"/books/{book.Id}/ebook/formats/{formats.Single(format => format.Format == "PDF").Id}");
            Assert.Equal("%PDF-"u8.ToArray(), pdf[..5]);
            var azw3 = Path.Combine(work, "made.azw3");
            await File.WriteAllBytesAsync(azw3, await reader.GetByteArrayAsync($"/books/{book.Id}/ebook/formats/{formats.Single(format => format.Format == "AZW3").Id}"));
            // Made with Shelf's details, not the file's own.
            var kindleMeta = await RunAsync("ebook-meta", azw3);
            Assert.Matches(@"Title\s*:\s*Moby-Dick; or, The Whale", kindleMeta);
            Assert.Matches(@"Author\(s\)\s*:\s*Herman Melville", kindleMeta);
            Assert.Contains("isbn:9780142437247", kindleMeta);

            // The details written into the downloaded EPUB, as Calibre reads them.
            var epub = Path.Combine(work, "download.epub");
            await File.WriteAllBytesAsync(epub, await reader.GetByteArrayAsync($"/books/{book.Id}/ebook/download"));
            var meta = await RunAsync("ebook-meta", epub);
            Assert.Matches(@"Title\s*:\s*Moby-Dick; or, The Whale", meta);
            Assert.Matches(@"Author\(s\)\s*:\s*Herman Melville", meta);
            Assert.Matches(@"Series\s*:\s*American Classics #3", meta);
            Assert.Matches(@"Publisher\s*:\s*Harper", meta);
            Assert.Matches(@"Languages\s*:\s*eng", meta);
            Assert.Contains("9780142437247", meta);

            // The folder export, added to a Calibre library one book to a folder.
            var folders = Path.Combine(work, "folders");
            ZipFile.ExtractToDirectory(new MemoryStream(await reader.GetByteArrayAsync("/books/export/folders")), folders);
            var library = Path.Combine(work, "library");
            Directory.CreateDirectory(library);
            await RunAsync("calibredb", "add", "--recurse", "--one-book-per-directory", folders, "--library-path", library);
            var listed = await RunAsync("calibredb", "list", "--fields", "title,authors,series,series_index,rating,tags,languages,formats", "--for-machine", "--library-path", library);
            using var json = JsonDocument.Parse(listed);
            var added = Assert.Single(json.RootElement.EnumerateArray());
            Assert.Equal("Moby-Dick; or, The Whale", added.GetProperty("title").GetString());
            Assert.Equal("Herman Melville", added.GetProperty("authors").GetString());
            Assert.Equal("American Classics", added.GetProperty("series").GetString());
            Assert.Equal(3, added.GetProperty("series_index").GetDouble());
            Assert.Equal(8, added.GetProperty("rating").GetDouble());
            Assert.Equal(["sea", "whales"], added.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).Order());
            var kinds = added.GetProperty("formats").EnumerateArray().Select(path => Path.GetExtension(path.GetString())!.ToLowerInvariant()).Order().ToArray();
            Assert.Equal([".azw3", ".epub", ".mobi", ".pdf"], kinds);

            // Custom columns made by Calibre itself come back as fields of their kinds.
            await RunAsync("calibredb", "add_custom_column", "--library-path", library, "copies", "Copies", "int");
            await RunAsync("calibredb", "add_custom_column", "--library-path", library, "signed", "Signed", "bool");
            await RunAsync("calibredb", "add_custom_column", "--library-path", library, "room", "Room", "text");
            var id = added.GetProperty("id").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
            await RunAsync("calibredb", "set_custom", "--library-path", library, "copies", id, "3");
            await RunAsync("calibredb", "set_custom", "--library-path", library, "signed", id, "true");
            await RunAsync("calibredb", "set_custom", "--library-path", library, "room", id, "Attic");
            var custom = Assert.Single(CalibreLibrary.Read(library)).Custom!;
            Assert.Equal([("Copies", FieldKind.Number, "3"), ("Room", FieldKind.Text, "Attic"), ("Signed", FieldKind.YesNo, "yes")],
                custom.Select(value => (value.Field, value.Kind, value.Value)).OrderBy(value => value.Field));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
