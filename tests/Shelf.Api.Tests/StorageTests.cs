using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class StorageTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Each_readers_files_are_measured_and_a_limit_refuses_what_will_not_fit()
    {
        var reader = await factory.SignUpAsync("Storage Reader");
        var small = Zip(600 * 1024);
        var readerId = await factory.ReaderIdAsync("Storage Reader");
        var first = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Small", "Someone", BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        var second = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Large", "Someone", BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        await reader.PostAsync($"/books/{first.Id}/ebook", new MultipartFormDataContent { { new ByteArrayContent(small), "file", "small.epub" } });
        var storage = factory.Services.GetRequiredService<Storage>();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var use = (await storage.ReportAsync(db)).Readers.Single(item => item.ReaderId == readerId);
            Assert.Equal(((long)small.Length, 0L, (int?)null), (use.Ebooks, use.Audio, use.LimitMegabytes));
            await Storage.SetLimitAsync(db, readerId, 1);
        }

        // 600 KB and 600 KB more is past a megabyte: the second is refused and its file goes.
        var refused = await reader.PostAsync($"/books/{second.Id}/ebook", new MultipartFormDataContent { { new ByteArrayContent(Zip(600 * 1024)), "file", "large.epub" } });
        Assert.EndsWith("ebook=full", refused.RequestMessage!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Null((await reader.GetFromJsonAsync<BookResponse>($"/books/{second.Id}", JsonOptions))!.EbookFileName);

        // A smaller file in place of the first fits.
        var replaced = await reader.PostAsync($"/books/{first.Id}/ebook", new MultipartFormDataContent { { new ByteArrayContent(Zip(700 * 1024)), "file", "bigger.epub" } });
        Assert.EndsWith("ebook=saved", replaced.RequestMessage!.RequestUri!.Query, StringComparison.Ordinal);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await Storage.SetLimitAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), readerId, null);
        }

        var allowed = await reader.PostAsync($"/books/{second.Id}/ebook", new MultipartFormDataContent { { new ByteArrayContent(Zip(600 * 1024)), "file", "large.epub" } });
        Assert.EndsWith("ebook=saved", allowed.RequestMessage!.RequestUri!.Query, StringComparison.Ordinal);
    }

    // A zip about the size asked, as an e-book must open as one; its contents differ each time, so no two are the same file.
    private static byte[] Zip(int size)
    {
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("filler", System.IO.Compression.CompressionLevel.NoCompression).Open();
            var bytes = new byte[size];
            Random.Shared.NextBytes(bytes);
            entry.Write(bytes);
        }

        return memory.ToArray();
    }
}
