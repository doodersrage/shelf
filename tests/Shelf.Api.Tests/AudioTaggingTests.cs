using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class AudioTaggingTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // A few seconds of tone, made by ffmpeg; null where there is no ffmpeg.
    private static async Task<byte[]?> ToneAsync(string extension, string folder)
    {
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
        List<string> arguments = ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=2"];
        arguments.AddRange(extension == ".mp3" ? ["-c:a", "libmp3lame", "-b:a", "32k"] : ["-c:a", "aac", "-b:a", "32k", "-f", "mp4"]);
        arguments.Add(path);
        return await Tools.RunAsync("ffmpeg", arguments, TimeSpan.FromMinutes(1), CancellationToken.None) is null ? null : await File.ReadAllBytesAsync(path);
    }

    private static async Task<Dictionary<string, string>> TagsAsync(byte[] bytes, string extension, string folder)
    {
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
        await File.WriteAllBytesAsync(path, bytes);
        var json = await Tools.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags", "-of", "json", path], TimeSpan.FromMinutes(1), CancellationToken.None);
        using var document = JsonDocument.Parse(json!);
        return document.RootElement.GetProperty("format").TryGetProperty("tags", out var tags)
            ? tags.EnumerateObject().ToDictionary(tag => tag.Name.ToLowerInvariant(), tag => tag.Value.GetString() ?? "")
            : [];
    }

    // One file comes down as itself with the book's details written in; tracks come down as a zip, each numbered
    // and tagged; the files kept on the shelf are the same as before; and no one else can have them.
    [Fact]
    public async Task The_recording_downloads_with_the_books_details_in_its_files()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-tagtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var m4b = await ToneAsync(".m4b", folder);
            var mp3 = await ToneAsync(".mp3", folder);
            if (m4b is null || mp3 is null)
            {
                return;
            }

            var reader = await factory.SignUpAsync("Tag Writer");
            var created = await reader.PostAsJsonAsync("/books", new CreateBookRequest("The Tagged Book", "Ann Writer", BookStatus.Reading, null, Year: 2001, Narrator: "Nat Reader", Series: "Tags", SeriesNumber: 2), JsonOptions);
            var book = (await created.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
            using (var content = new MultipartFormDataContent { { new ByteArrayContent(m4b), "file", "whole.m4b" } })
            {
                await reader.PostAsync($"/books/{book.Id}/audio", content);
            }

            var download = await reader.GetAsync($"/books/{book.Id}/audio/download");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("Ann Writer - The Tagged Book.m4b", download.Content.Headers.ContentDisposition!.FileNameStar);
            var tags = await TagsAsync(await download.Content.ReadAsByteArrayAsync(), ".m4b", folder);
            Assert.Equal(("The Tagged Book", "Ann Writer", "Nat Reader"), (tags["title"], tags["artist"], tags["composer"]));
            Assert.Equal(m4b, await reader.GetByteArrayAsync($"/books/{book.Id}/audio/tracks/0"));

            // In two tracks: a zip, each named and numbered.
            var tracked = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Tracks Tagged", "Ann Writer", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
            var tracks = new MemoryStream();
            using (var zip = new ZipArchive(tracks, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in new[] { "01 Opening.mp3", "02 Closing.mp3" })
                {
                    await using var entry = zip.CreateEntry(name).Open();
                    await entry.WriteAsync(mp3);
                }
            }

            using (var content = new MultipartFormDataContent { { new ByteArrayContent(tracks.ToArray()), "file", "tracks.zip" } })
            {
                await reader.PostAsync($"/books/{tracked.Id}/audio", content);
            }

            using var downloaded = new ZipArchive(new MemoryStream(await reader.GetByteArrayAsync($"/books/{tracked.Id}/audio/download")));
            var names = downloaded.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(2, names.Count);
            Assert.StartsWith("Ann Writer - Tracks Tagged/01 - ", names[0]);
            using var second = new MemoryStream();
            await using (var stream = downloaded.GetEntry(names[1])!.Open())
            {
                await stream.CopyToAsync(second);
            }

            var secondTags = await TagsAsync(second.ToArray(), ".mp3", folder);
            Assert.Equal(("Tracks Tagged", "2/2"), (secondTags["album"], secondTags["track"]));

            var stranger = await factory.SignUpAsync("Tag Stranger");
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/audio/download")).StatusCode);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
