using System.IO.Compression;
using System.Text;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class AudioLengthTests
{
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures"));

    [Fact]
    public void An_m4b_says_its_length_in_its_movie_header() =>
        Assert.Equal(12, AudioLength.Read(Path.Combine(Fixtures, "chapters.m4b"))!.Value, 1);

    [Fact]
    public void Mp3_tracks_say_their_lengths()
    {
        var folder = Directory.CreateTempSubdirectory("shelf-lengths-").FullName;
        try
        {
            ZipFile.ExtractToDirectory(Path.Combine(Fixtures, "three-tracks.zip"), folder);
            var lengths = Directory.GetFiles(folder, "*.mp3").Order(StringComparer.Ordinal).Select(AudioLength.Read).ToList();
            Assert.Equal([3.0, 4.0, 5.0], lengths.Select(length => Math.Round(length!.Value)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_wav_is_its_data_over_its_bytes_a_second()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shelf-{Guid.NewGuid():N}.wav");
        try
        {
            const int rate = 8000;
            var data = new byte[rate * 2 * 3];
            using (var file = new BinaryWriter(File.Create(path)))
            {
                file.Write(Encoding.ASCII.GetBytes("RIFF"));
                file.Write(36 + data.Length);
                file.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                file.Write(16);
                file.Write((short)1);
                file.Write((short)1);
                file.Write(rate);
                file.Write(rate * 2);
                file.Write((short)2);
                file.Write((short)16);
                file.Write(Encoding.ASCII.GetBytes("data"));
                file.Write(data.Length);
                file.Write(data);
            }

            Assert.Equal(3, AudioLength.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_that_does_not_say_has_no_length()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shelf-{Guid.NewGuid():N}.mp3");
        try
        {
            File.WriteAllBytes(path, new byte[64]);
            Assert.Null(AudioLength.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
