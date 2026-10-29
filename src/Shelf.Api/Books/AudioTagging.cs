using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Shelf.Api.Books;

// The recording to keep, with the shelf's details written into its files, as Audiobookshelf embeds them: the title,
// author, narrator, year, and cover, and in a single file its chapters too. ffmpeg copies the sound as it is, so
// nothing is re-encoded and it takes moments; the stored files are never changed. Without ffmpeg the files go as
// they are. One file goes as itself; a recording in tracks goes as a zip, each track numbered and named.
public static class AudioTagging
{
    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/{id:int}/audio/download", async Task<IResult> (int id, Data.ShelfDb db, AudioStore audio, CoverStore covers, EbookStore ebooks, M4bService m4b, CancellationToken cancellationToken) =>
        {
            if (await Lending.OpenAsync(db, id, cancellationToken) is not { } open || open.Book.AudioStoredName is not { } stored || audio.Tracks(stored).Count == 0)
            {
                return TypedResults.NotFound();
            }

            var (path, name, type) = await WriteAsync(open.Book, audio, covers, ebooks, m4b.Available ? m4b.Tool : null, cancellationToken);
            return new TempFileResult(path, type, name);
        }).WithTags("Files");

    // A file in a temporary place, for the caller to send and delete, with the name to send it under.
    public static async Task<(string Path, string Name, string ContentType)> WriteAsync(Book book, AudioStore audio, CoverStore covers, EbookStore ebooks, string? ffmpeg, CancellationToken cancellationToken)
    {
        var stored = book.AudioStoredName!;
        var tracks = audio.Tracks(stored);
        var baseName = NotesExport.FileName(book)[..^".md".Length];
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-tags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var cover = Covers.Picture(book, ebooks, covers);
            string? coverPath = null;
            if (cover is not null)
            {
                coverPath = Path.Combine(folder, "cover.jpg");
                await File.WriteAllBytesAsync(coverPath, cover, cancellationToken);
            }

            if (tracks.Count == 1)
            {
                var source = audio.TrackPath(stored, 0)!;
                var output = TempFileResult.NewPath(Path.GetExtension(source).ToLowerInvariant());
                var chapters = Chapters(book, audio);
                if (ffmpeg is null || !await TagAsync(ffmpeg, source, output, folder, Tags(book, null, null), chapters, coverPath, cancellationToken))
                {
                    File.Copy(source, output, overwrite: true);
                }

                return (output, baseName + Path.GetExtension(source).ToLowerInvariant(), AudioStore.ContentType(source));
            }

            var zipPath = TempFileResult.NewPath(".zip");
            await using (var file = File.Create(zipPath))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var width = tracks.Count.ToString(CultureInfo.InvariantCulture).Length;
                foreach (var track in tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = audio.TrackPath(stored, track.Index)!;
                    var extension = Path.GetExtension(source).ToLowerInvariant();
                    var tagged = Path.Combine(folder, "track" + extension);
                    var entry = $"{baseName}/{(track.Index + 1).ToString(new string('0', Math.Max(2, width)), CultureInfo.InvariantCulture)} - {Safe(AudioStore.PlainTitle(track.Title))}{extension}";
                    if (ffmpeg is not null && await TagAsync(ffmpeg, source, tagged, folder, Tags(book, track, tracks.Count), [], coverPath, cancellationToken))
                    {
                        zip.CreateEntryFromFile(tagged, entry, CompressionLevel.NoCompression);
                        File.Delete(tagged);
                    }
                    else
                    {
                        zip.CreateEntryFromFile(source, entry, CompressionLevel.NoCompression);
                    }
                }
            }

            return (zipPath, baseName + ".zip", "application/zip");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // What every player reads: the book as the album, its author as the artist, the narrator as the composer, as
    // Audiobookshelf and most audiobook apps take them.
    public static Dictionary<string, string> Tags(Book book, AudioTrack? track, int? count)
    {
        var tags = new Dictionary<string, string>
        {
            ["title"] = track is null ? book.Title : AudioStore.PlainTitle(track.Title),
            ["album"] = book.Title,
            ["artist"] = book.Author,
            ["album_artist"] = book.Author,
            ["genre"] = "Audiobook",
        };
        if (book.Narrator is { Length: > 0 } narrator)
        {
            tags["composer"] = narrator;
        }

        if (book.Year is int year)
        {
            tags["date"] = year.ToString(CultureInfo.InvariantCulture);
        }

        if (book.Series is { Length: > 0 } series)
        {
            tags["grouping"] = book.SeriesNumber is int number ? $"{series} #{number}" : series;
        }

        if (track is not null && count is int total)
        {
            tags["track"] = $"{track.Index + 1}/{total}";
        }

        return tags;
    }

    // The player's chapters in a single file: Audible's or set by hand, else the file's own marks.
    private static List<(string Title, double Start)> Chapters(Book book, AudioStore audio)
    {
        var lengths = audio.Lengths(book.AudioStoredName);
        return Audible.Place(book.AudioChapterNames, lengths) is { } placed
            ? [.. placed.Select(chapter => (chapter.Title, Start: lengths.Take(chapter.Track).Sum(length => length ?? 0) + chapter.Start))]
            : [.. audio.Chapters(book.AudioStoredName).Select(chapter => (chapter.Title, chapter.Start))];
    }

    private static async Task<bool> TagAsync(string ffmpeg, string source, string output, string folder, Dictionary<string, string> tags, List<(string Title, double Start)> chapters,
        string? cover, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var metadata = new StringBuilder(";FFMETADATA1\n");
        foreach (var (key, value) in tags)
        {
            metadata.Append(key).Append('=').Append(Escape(value)).Append('\n');
        }

        // Chapters only where the container keeps them: MP4's kinds and Matroska; MP3 players mostly ignore them.
        var mp4 = extension is ".m4b" or ".m4a" or ".mp4";
        var total = AudioLength.Read(source) ?? 0;
        if (mp4 && chapters.Count > 1)
        {
            for (var i = 0; i < chapters.Count; i++)
            {
                var end = i + 1 < chapters.Count ? chapters[i + 1].Start : Math.Max(total, chapters[i].Start + 1);
                metadata.Append("[CHAPTER]\nTIMEBASE=1/1000\n")
                    .Append("START=").Append((long)(chapters[i].Start * 1000)).Append('\n')
                    .Append("END=").Append((long)(Math.Max(end, chapters[i].Start + 1) * 1000)).Append('\n')
                    .Append("title=").Append(Escape(chapters[i].Title)).Append('\n');
            }
        }

        var metadataPath = Path.Combine(folder, "tags.txt");
        await File.WriteAllTextAsync(metadataPath, metadata.ToString(), cancellationToken);
        List<string> arguments = ["-nostdin", "-v", "error", "-y", "-i", source, "-i", metadataPath];
        // A cover goes into the kinds of file that carry one as a picture: MP3 and MP4's.
        if (cover is not null && (mp4 || extension == ".mp3"))
        {
            arguments.AddRange(["-i", cover, "-map", "0:a", "-map", "2:v", "-c:v", "mjpeg", "-disposition:v", "attached_pic"]);
        }
        else
        {
            arguments.AddRange(["-map", "0:a"]);
        }

        arguments.AddRange(["-map_metadata", "1", "-map_chapters", mp4 && chapters.Count > 1 ? "1" : "0", "-c:a", "copy"]);
        if (extension == ".mp3")
        {
            arguments.AddRange(["-id3v2_version", "3"]);
        }

        if (mp4)
        {
            arguments.AddRange(["-movflags", "+faststart", "-f", "mp4"]);
        }

        arguments.Add(output);
        if (await Tools.RunAsync(ffmpeg, arguments, TimeSpan.FromMinutes(30), cancellationToken) is not null && File.Exists(output) && new FileInfo(output).Length > 0)
        {
            return true;
        }

        File.Delete(output);
        return false;
    }

    private static string Safe(string text)
    {
        var clean = new string(text.Select(letter => Path.GetInvalidFileNameChars().Contains(letter) || letter is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : letter).ToArray()).Trim();
        return clean.Length == 0 ? "track" : clean.Length > 80 ? clean[..80] : clean;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\n", " ");
}
