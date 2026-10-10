using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Shelf.Api.Books;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

// A restore drill: fill a shelf, take an admin's snapshot through the real endpoint, unpack it into an empty data
// folder as the docs say, start a second shelf on it, and check that everything came back.
public sealed class RestoreTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private readonly List<string> folders = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_snapshot_restored_into_an_empty_folder_brings_back_every_reader_book_file_and_note()
    {
        // The shelf as it was.
        var admin = factory.Client;
        var epub = ImportTests.Epub("Restored Book", "Someone", null, "en", cover: false);
        var audio = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures", "chapters.m4b"));
        var book = await (await admin.PostAsJsonAsync("/books", new CreateBookRequest("Restored Book", "Someone", BookStatus.Reading, null, Pages: 300), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        await Upload(admin, $"/books/{book!.Id}/ebook", "restored.epub", epub);
        await Upload(admin, $"/books/{book.Id}/audio", "restored.m4b", audio);
        await Upload(admin, $"/books/{book.Id}/cover", "cover.png", Png);
        Assert.True((await admin.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("a passage worth keeping", 0, "my note"), JsonOptions)).IsSuccessStatusCode);
        Assert.True((await admin.PostAsJsonAsync($"/books/{book.Id}/quotes", new CreateQuoteRequest("A line to remember.", 12), JsonOptions)).IsSuccessStatusCode);
        Assert.True((await admin.PostAsJsonAsync($"/books/{book.Id}/sessions", new CreateSessionRequest(new DateOnly(2026, 10, 1), 1, 40, "a good start"), JsonOptions)).IsSuccessStatusCode);

        // A second reader with two-step sign-in.
        var careful = await factory.SignUpAsync("Careful Restorer");
        var start = await (await careful.PostAsync("/account/two-factor/start", null)).Content.ReadFromJsonAsync<TwoFactorStart>(JsonOptions);
        var confirmed = await careful.PostAsJsonAsync("/account/two-factor/confirm", new TwoFactorCodeRequest(TwoFactor.Code(start!.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30)), JsonOptions);
        var recovery = (await confirmed.Content.ReadFromJsonAsync<RecoveryCodes>(JsonOptions))!.Codes;

        // The snapshot, as an admin downloads it from Readers.
        var snapshot = await admin.GetAsync("/admin/snapshot");
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        var zip = await snapshot.Content.ReadAsByteArrayAsync();

        // Restored with the keys folder put back: everything, sessions included, as it was.
        await using (var restored = Restore(zip, keys: factory.KeysRoot))
        {
            var reader = restored.CreateClient();
            await SignInAsync(reader, "Tenar");
            var books = await reader.GetFromJsonAsync<List<BookResponse>>("/books", JsonOptions);
            var back = Assert.Single(books!, item => item.Title == "Restored Book");
            Assert.Equal((BookStatus.Reading, 300, "restored.epub"), (back.Status, back.Pages, back.EbookFileName));
            Assert.Equal(epub, await reader.GetByteArrayAsync($"/books/{back.Id}/ebook/file"));
            Assert.Equal(audio, await reader.GetByteArrayAsync($"/books/{back.Id}/audio/tracks/0"));
            Assert.Equal(Png, await reader.GetByteArrayAsync($"/books/{back.Id}/cover"));
            var marks = await reader.GetFromJsonAsync<List<HighlightResponse>>($"/books/{back.Id}/highlights", JsonOptions);
            Assert.Equal(("a passage worth keeping", "my note"), (Assert.Single(marks!).Text, marks![0].Note));
            Assert.Contains("A line to remember.", await reader.GetStringAsync($"/books/{back.Id}/quotes"));
            Assert.Contains("a good start", await reader.GetStringAsync($"/books/{back.Id}"));

            // Two-step sign-in still works, since the keys that seal its secret came back too.
            var device = restored.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            Assert.Equal("/signin/code", (await PasswordAsync(device, "Careful Restorer")).Headers.Location?.OriginalString);
            Assert.Equal("/", (await ShelfApiFactory.PostFormAsync(device, "/signin/code", "/account/signin/code", new() { ["code"] = TwoFactor.Code(start.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30) })).Headers.Location?.OriginalString);
        }

        // Restored without the keys folder, as the docs warn: passwords still work, and a reader with two-step
        // sign-in gets in with a recovery code.
        await using (var bare = Restore(zip, keys: null))
        {
            var reader = bare.CreateClient();
            await SignInAsync(reader, "Tenar");
            Assert.Contains("Restored Book", await reader.GetStringAsync("/books"));

            var device = bare.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            Assert.Equal("/signin/code", (await PasswordAsync(device, "Careful Restorer")).Headers.Location?.OriginalString);
            Assert.Equal("/", (await ShelfApiFactory.PostFormAsync(device, "/signin/code", "/account/signin/code", new() { ["code"] = recovery[0] })).Headers.Location?.OriginalString);

            // Signed in, they turn two-step off with another recovery code, to set it up again with their phone.
            Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync("/account/two-factor/disable", new TwoFactorCodeRequest(recovery[1]), JsonOptions)).StatusCode);
        }
    }

    // A second shelf on a fresh data folder holding the unpacked snapshot, and the old keys if given.
    private RestoredShelf Restore(byte[] snapshot, string? keys)
    {
        var data = Path.Combine(Path.GetTempPath(), $"shelf-restored-{Guid.NewGuid():N}");
        folders.Add(data);
        Directory.CreateDirectory(data);
        using (var archive = new ZipArchive(new MemoryStream(snapshot)))
        {
            archive.ExtractToDirectory(data);
        }

        Assert.True(File.Exists(Path.Combine(data, "shelf.db")));
        if (keys is not null)
        {
            CopyFolder(keys, Path.Combine(data, "keys"));
        }

        return new RestoredShelf(data);
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    private static async Task SignInAsync(HttpClient client, string name)
    {
        var signedIn = await ShelfApiFactory.PostFormAsync(client, "/signin", "/account/signin", new() { ["name"] = name, ["password"] = ShelfApiFactory.Password });
        Assert.Equal("/", signedIn.RequestMessage?.RequestUri?.AbsolutePath);
    }

    private static Task<HttpResponseMessage> PasswordAsync(HttpClient client, string name) =>
        ShelfApiFactory.PostFormAsync(client, "/signin", "/account/signin", new() { ["name"] = name, ["password"] = ShelfApiFactory.Password });

    private static async Task Upload(HttpClient client, string path, string name, byte[] bytes)
    {
        using var content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
        var response = await client.PostAsync(path, content);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Redirect, $"{path}: {(int)response.StatusCode}");
    }

    private sealed class RestoredShelf(string data) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Shelf", $"Data Source={Path.Combine(data, "shelf.db")}");
            builder.UseSetting("EbookStore:Root", Path.Combine(data, "ebooks"));
            builder.UseSetting("AudioStore:Root", Path.Combine(data, "audio"));
            builder.UseSetting("CoverStore:Root", Path.Combine(data, "covers"));
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(data, "keys"));
            builder.UseSetting("Accounts:SignInsPerMinute", "1000");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseEnvironment("Testing");
        }
    }
}
