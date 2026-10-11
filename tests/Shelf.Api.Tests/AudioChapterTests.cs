using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class AudioChapterTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task An_m4b_audiobook_gives_its_chapters_from_either_kind_of_mark()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-m4b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var book = await MakeAudiobookAsync(folder);
            if (book is null)
            {
                // Without ffmpeg there is nothing to read; a plain file still has no chapters.
                var plain = Path.Combine(folder, "plain.m4b");
                await File.WriteAllBytesAsync(plain, [0, 0, 0, 8, (byte)'f', (byte)'r', (byte)'e', (byte)'e']);
                Assert.Empty(AudioChapters.Read(plain));
                return;
            }

            var nero = AudioChapters.Read(book);
            Assert.Equal(["Opening", "The Middle Part", "Ending"], nero.Select(chapter => chapter.Title));
            Assert.Equal([0.0, 4.0, 9.0], nero.Select(chapter => Math.Round(chapter.Start, 1)));

            // Hide the Nero list, so only the QuickTime chapter track is left to read.
            var bytes = await File.ReadAllBytesAsync(book);
            var at = IndexOf(bytes, "chpl"u8.ToArray());
            Assert.True(at > 0);
            "free"u8.CopyTo(bytes.AsSpan(at));
            var quickTime = Path.Combine(folder, "quicktime.m4b");
            await File.WriteAllBytesAsync(quickTime, bytes);
            var track = AudioChapters.Read(quickTime);
            Assert.Equal(["Opening", "The Middle Part", "Ending"], track.Select(chapter => chapter.Title));
            Assert.Equal([0.0, 4.0, 9.0], track.Select(chapter => Math.Round(chapter.Start, 1)));

            // On the shelf, the player lists them.
            var created = await (await factory.Client.PostAsJsonAsync("/books", new CreateBookRequest("A Chaptered Recording", "Someone", BookStatus.Reading, null), JsonOptions))
                .Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
            using (var content = new MultipartFormDataContent { { new ByteArrayContent(await File.ReadAllBytesAsync(book)), "file", "book.m4b" } })
            {
                await factory.Client.PostAsync($"/books/{created!.Id}/audio", content);
            }

            var player = await factory.Client.GetStringAsync($"/library/{created.Id}/listen");
            Assert.Matches("The Middle Part</span>\\s*<span class=\"hint\">0:04", player);
            Assert.Contains("Bookmark this moment", player);
            var plan = await factory.Client.GetFromJsonAsync<AudioPlan>($"/books/{created.Id}/audio/plan", JsonOptions);
            Assert.Equal([0.0, 4.0, 9.0], plan!.Chapters.Select(chapter => Math.Round(chapter.Start, 1)));
            Assert.Equal(12, plan.Tracks.Single().Length!.Value, 1);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Twelve seconds of tone in three chapters, made by ffmpeg the way audiobook tools make them.
    private static async Task<string?> MakeAudiobookAsync(string folder)
    {
        var chapters = Path.Combine(folder, "chapters.txt");
        await File.WriteAllTextAsync(chapters, """
            ;FFMETADATA1
            [CHAPTER]
            TIMEBASE=1/1000
            START=0
            END=4000
            title=Opening
            [CHAPTER]
            TIMEBASE=1/1000
            START=4000
            END=9000
            title=The Middle Part
            [CHAPTER]
            TIMEBASE=1/1000
            START=9000
            END=12000
            title=Ending
            """.Replace("            ", ""));
        var output = Path.Combine(folder, "book.m4b");
        try
        {
            using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
                ["-y", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=12", "-i", chapters,
                 "-map_metadata", "1", "-map_chapters", "1", "-c:a", "aac", "-b:a", "32k", output])
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            await ffmpeg.WaitForExitAsync();
            return ffmpeg.ExitCode == 0 && File.Exists(output) ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {
                return index;
            }
        }

        return -1;
    }
}
