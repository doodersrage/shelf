using System.IO.Compression;
using System.Security.Cryptography;

namespace Shelf.Api.Books;

public sealed class AudioStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    public const long MaxBytes = 1024L * 1024 * 1024;
    private const int MaxTracks = 200;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".ogg", ".opus", ".wav", ".flac",
    };

    public string Root { get; } = configuration["AudioStore:Root"]
        ?? Path.Combine(environment.ContentRootPath, "audio");

    // Tracks are played in file-name order unless the caller already knows the order (a catalog's, say).
    public async Task<AudioSave> SaveAsync(IReadOnlyList<AudioUpload> files, CancellationToken cancellationToken, bool keepOrder = false)
    {
        if (files.Count == 0 || files.Sum(file => file.Length) > MaxBytes)
        {
            return files.Count == 0
                ? new AudioSave(AudioSaveStatus.Unsupported, null, null)
                : new AudioSave(AudioSaveStatus.TooLarge, null, null);
        }

        Directory.CreateDirectory(Root);
        var storedName = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Root, storedName);
        Directory.CreateDirectory(directory);
        try
        {
            string? display;
            if (files.Count == 1 && IsZip(files[0].Name))
            {
                var expanded = await ExpandZipAsync(directory, files[0], cancellationToken);
                if (expanded.TooLarge)
                {
                    Directory.Delete(directory, recursive: true);
                    return new AudioSave(AudioSaveStatus.TooLarge, null, null);
                }

                display = expanded.Display;
            }
            else
            {
                var audio = files.Where(file => IsAudio(file.Name)).ToList();
                if (!keepOrder)
                {
                    audio = audio.OrderBy(file => Path.GetFileName(file.Name), StringComparer.Ordinal).ToList();
                }
                if (audio.Count == 0)
                {
                    Directory.Delete(directory, recursive: true);
                    return new AudioSave(AudioSaveStatus.Unsupported, null, null);
                }

                var count = Math.Min(audio.Count, MaxTracks);
                long total = 0;
                for (var index = 0; index < count; index++)
                {
                    total = await WriteTrackAsync(directory, index, audio[index].Name, audio[index].Content, total, cancellationToken);
                    if (total < 0)
                    {
                        Directory.Delete(directory, recursive: true);
                        return new AudioSave(AudioSaveStatus.TooLarge, null, null);
                    }
                }

                display = count == 1 ? DisplayName(audio[0].Name) : $"{count} tracks";
            }

            if (display is null || TrackFiles(storedName).Count == 0)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return new AudioSave(AudioSaveStatus.Unsupported, null, null);
            }

            return new AudioSave(AudioSaveStatus.Saved, storedName, display);
        }
        catch
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            throw;
        }
    }

    public IReadOnlyList<AudioTrack> Tracks(string? storedName)
    {
        var files = TrackFiles(storedName);
        return files.Select((path, index) => new AudioTrack(index, TrackTitle(path))).ToList();
    }

    // The chapter marks inside a single-file audiobook (an .m4b, say); empty for a zip of tracks or a file without them.
    public IReadOnlyList<AudioChapter> Chapters(string? storedName)
    {
        var files = TrackFiles(storedName);
        return files.Count == 1 ? AudioChapters.Read(files[0]) : [];
    }

    public string? TrackPath(string? storedName, int index)
    {
        var files = TrackFiles(storedName);
        return index >= 0 && index < files.Count ? files[index] : null;
    }

    // How long each track runs, in seconds; null for one whose file does not say. Stored files never change, so the
    // answer is kept for as long as the app runs.
    public IReadOnlyList<double?> Lengths(string? storedName) =>
        TrackFiles(storedName).Select(path => lengths.GetOrAdd(path, AudioLength.Read)).ToList();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double?> lengths = new(StringComparer.Ordinal);

    // The recording's size in bytes, all tracks together; 0 when there is none.
    public long Size(string? storedName) => TrackFiles(storedName).Sum(path => new FileInfo(path).Length);

    // Where an upload waits while the reader decides whether to keep a second copy of a recording.
    public string HeldRoot => Path.Combine(Root, "held");

    public bool Hold(string storedName) => Move(OpenDirectory(storedName), HeldPath(storedName));

    public bool Release(string storedName) => Move(HeldPath(storedName), Path.Combine(Root, storedName));

    public void Forget(string storedName)
    {
        if (HeldPath(storedName) is { } path && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            File.Delete(path + Fingerprint.Extension);
        }
    }

    private string? HeldPath(string storedName) =>
        storedName.Length == 32 && storedName.All(Uri.IsHexDigit) ? Path.Combine(HeldRoot, storedName) : null;

    private static bool Move(string? from, string? to)
    {
        if (from is null || to is null || !Directory.Exists(from))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Directory.Move(from, to);
        if (File.Exists(from + Fingerprint.Extension))
        {
            File.Move(from + Fingerprint.Extension, to + Fingerprint.Extension, overwrite: true);
        }

        return true;
    }

    public async Task<string?> HashAsync(string? storedName, CancellationToken cancellationToken)
    {
        var files = TrackFiles(storedName);
        if (files.Count == 0)
        {
            return null;
        }

        // Kept beside the recording's folder, not in it, so it is never taken for a track.
        return await Fingerprint.ReadOrComputeAsync(OpenDirectory(storedName)! + Fingerprint.Extension, () => ComputeAsync(files, cancellationToken), cancellationToken);
    }

    private static async Task<string?> ComputeAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                incremental.AppendData(buffer.AsSpan(0, read));
            }
        }

        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    public async Task WriteZipAsync(string? storedName, Stream destination, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var track in Tracks(storedName))
        {
            var path = TrackPath(storedName, track.Index);
            if (path is null)
            {
                continue;
            }

            var title = track.Title.Replace('/', '-').Replace('\\', '-');
            var entry = zip.CreateEntry($"{track.Index:000}-{title}", CompressionLevel.NoCompression);
            await using var input = File.OpenRead(path);
            await using var output = entry.Open();
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    public void Delete(string? storedName)
    {
        var directory = OpenDirectory(storedName);
        if (directory is null)
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }

        Fingerprint.Forget(directory + Fingerprint.Extension);
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".m4b" or ".aac" => "audio/mp4",
        ".ogg" or ".opus" => "audio/ogg",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        _ => "application/octet-stream",
    };

    private List<string> TrackFiles(string? storedName)
    {
        var directory = OpenDirectory(storedName);
        if (directory is null)
        {
            return [];
        }

        return Directory.EnumerateFiles(directory)
            .Where(path => IsAudio(path))
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToList();
    }

    private string? OpenDirectory(string? storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName))
        {
            return null;
        }

        var name = Path.GetFileName(storedName);
        if (!string.Equals(name, storedName, StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var root = Path.GetFullPath(Root);
        var full = Path.GetFullPath(Path.Combine(root, name));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) || !Directory.Exists(full))
        {
            return null;
        }

        return full;
    }

    private async Task<(string? Display, bool TooLarge)> ExpandZipAsync(string directory, AudioUpload zip, CancellationToken cancellationToken)
    {
        var archivePath = Path.Combine(directory, "upload.zip");
        var total = await CopyAsync(zip.Content, archivePath, 0, cancellationToken);
        if (total < 0)
        {
            return (null, true);
        }

        var entries = new List<ZipArchiveEntry>();
        var tooLarge = false;
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.FullName.Contains("..", StringComparison.Ordinal) || !IsAudio(entry.Name))
                {
                    continue;
                }

                entries.Add(entry);
            }

            entries.Sort((left, right) => string.Compare(left.FullName, right.FullName, StringComparison.Ordinal));
            long written = 0;
            for (var index = 0; index < entries.Count && index < MaxTracks; index++)
            {
                await using var source = entries[index].Open();
                written = await WriteTrackAsync(directory, index, entries[index].Name, source, written, cancellationToken);
                if (written < 0)
                {
                    tooLarge = true;
                    break;
                }
            }
        }

        File.Delete(archivePath);
        return tooLarge ? (null, true) : (entries.Count == 0 ? null : DisplayName(zip.Name), false);
    }

    private static async Task<long> WriteTrackAsync(
        string directory,
        int index,
        string originalName,
        Stream source,
        long total,
        CancellationToken cancellationToken)
    {
        var stored = $"{index:000}-{SafeFileName(originalName)}";
        return await CopyAsync(source, Path.Combine(directory, stored), total, cancellationToken);
    }

    private static async Task<long> CopyAsync(Stream source, string path, long total, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return total;
            }

            total += read;
            if (total > MaxBytes)
            {
                output.Close();
                File.Delete(path);
                return -1;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static string DisplayName(string original)
    {
        var name = Path.GetFileName(original.Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "audiobook";
        }

        return name.Length <= 200 ? name : name[^200..];
    }

    private static string SafeFileName(string original)
    {
        var name = DisplayName(original);
        foreach (var character in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(character, '-');
        }

        return name.Length <= 80 ? name : name[^80..];
    }

    private static string TrackTitle(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 4 && char.IsDigit(name[0]) && name[3] == '-' ? name[4..] : name;
    }

    private static bool IsZip(string name) =>
        Path.GetExtension(name).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudio(string name) => Extensions.Contains(Path.GetExtension(name));
}

public enum AudioSaveStatus
{
    Saved,
    Unsupported,
    TooLarge,
}

public sealed record AudioSave(AudioSaveStatus Status, string? StoredName, string? FileName);

public sealed record AudioUpload(string Name, Stream Content, long Length);

public sealed record AudioTrack(int Index, string Title);
