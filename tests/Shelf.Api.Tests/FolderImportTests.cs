using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class FolderImportTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"shelf-import-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Books_dropped_in_a_readers_folder_land_on_their_shelf_once_they_stop_changing()
    {
        await using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Import:Folder", folder);
            builder.UseSetting("Import:SettleSeconds", "0");
            builder.UseSetting("Import:EverySeconds", "3600");
        });
        var reader = app.CreateClient();
        await ShelfApiFactory.PostFormAsync(reader, "/signup", "/account/signup", new() { ["name"] = "Dropping Reader", ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
        var import = app.Services.GetRequiredService<FolderImport>();
        await import.ScanAsync(CancellationToken.None);

        var mine = Path.Combine(folder, "dropping reader");
        Directory.CreateDirectory(mine);
        await File.WriteAllBytesAsync(Path.Combine(mine, "left-hand.epub"), ImportTests.Epub("The Left Hand of Darkness", "Ursula K. Le Guin", null, "en", cover: false));
        await File.WriteAllTextAsync(Path.Combine(mine, "shopping.txt"), "eggs");
        await File.WriteAllBytesAsync(Path.Combine(mine, "half.epub.part"), [1, 2, 3]);

        // The first look only notes what is there; it is taken when it looks the same the next time.
        Assert.Equal(0, await import.ScanAsync(CancellationToken.None));
        Assert.Equal(2, await import.ScanAsync(CancellationToken.None));

        var books = await reader.GetFromJsonAsync<List<BookResponse>>("/books", JsonOptions);
        var added = Assert.Single(books!);
        Assert.Equal(("The Left Hand of Darkness", "Ursula K. Le Guin"), (added.Title, added.Author));
        Assert.True(File.Exists(Path.Combine(mine, FolderImport.Imported, "left-hand.epub")));
        Assert.True(File.Exists(Path.Combine(mine, FolderImport.NotAdded, "shopping.txt")));
        Assert.True(File.Exists(Path.Combine(mine, "half.epub.part")));
        Assert.False(File.Exists(Path.Combine(mine, "left-hand.epub")));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
            Assert.Contains(db.AuditEntries.IgnoreQueryFilters().AsEnumerable(), entry => entry.Action == "Added from the import folder" && entry.Detail == "The Left Hand of Darkness");
        }

        // The very same file again is already on the shelf: put away, with no second book.
        File.Copy(Path.Combine(mine, FolderImport.Imported, "left-hand.epub"), Path.Combine(mine, "again.epub"));
        await import.ScanAsync(CancellationToken.None);
        await import.ScanAsync(CancellationToken.None);
        Assert.Single((await reader.GetFromJsonAsync<List<BookResponse>>("/books", JsonOptions))!);
        Assert.True(File.Exists(Path.Combine(mine, FolderImport.Imported, "again.epub")));
    }

    [Fact]
    public async Task A_folder_of_tracks_becomes_one_audiobook_and_a_growing_file_waits()
    {
        await using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Import:Folder", folder);
            builder.UseSetting("Import:SettleSeconds", "0");
            builder.UseSetting("Import:EverySeconds", "3600");
            builder.UseSetting("Import:AfterImport", "delete");
        });
        var reader = app.CreateClient();
        await ShelfApiFactory.PostFormAsync(reader, "/signup", "/account/signup", new() { ["name"] = "Listening Dropper", ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
        var import = app.Services.GetRequiredService<FolderImport>();
        var album = Path.Combine(folder, "Listening Dropper", "A Long Story");
        Directory.CreateDirectory(album);
        await File.WriteAllBytesAsync(Path.Combine(album, "01.mp3"), [0xFF, 0xFB, 0x90, 0x00, 1]);
        await File.WriteAllBytesAsync(Path.Combine(album, "02.mp3"), [0xFF, 0xFB, 0x90, 0x00, 2]);
        await import.ScanAsync(CancellationToken.None);

        // Still being copied: a third track arrives between looks, so the folder waits for another.
        await File.WriteAllBytesAsync(Path.Combine(album, "03.mp3"), [0xFF, 0xFB, 0x90, 0x00, 3]);
        Assert.Equal(0, await import.ScanAsync(CancellationToken.None));
        Assert.Equal(1, await import.ScanAsync(CancellationToken.None));

        var book = Assert.Single((await reader.GetFromJsonAsync<List<BookResponse>>("/books", JsonOptions))!);
        Assert.Equal(BookFormat.Audiobook, book.Format);
        Assert.False(Directory.Exists(album));
    }
}
