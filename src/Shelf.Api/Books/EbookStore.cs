using System.Security.Cryptography;

namespace Shelf.Api.Books;

public sealed class EbookStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    // The most one e-book file may be: 80 MB unless Uploads:MaxEbookMegabytes says otherwise.
    public static long MaxBytes { get; private set; } = 80L * 1024 * 1024;

    public static void SetLimit(int? megabytes)
    {
        if (megabytes is > 0)
        {
            MaxBytes = Math.Min(megabytes.Value, 1024 * 1024) * 1024L * 1024;
        }
    }

    public string Root { get; } = configuration["EbookStore:Root"]
        ?? Path.Combine(environment.ContentRootPath, "ebooks");

    // Kindle files are turned into EPUBs on the way in, by Calibre's ebook-convert when it is installed.
    public string Converter => configuration["Ebooks:Convert"] ?? "ebook-convert";

    public static bool IsKindle(string name) => Path.GetExtension(name).ToLowerInvariant() is ".mobi" or ".azw3" or ".azw" or ".kfx";

    // keepAsIs: another format of a book, kept as it came, so a Kindle file stays one for a Kindle.
    public async Task<EbookSave> SaveAsync(Stream source, string originalName, CancellationToken cancellationToken, bool keepAsIs = false)
    {
        var extension = Path.GetExtension(originalName).ToLowerInvariant();
        if (IsKindle(originalName) && !(keepAsIs && extension is ".mobi" or ".azw3" or ".azw"))
        {
            return keepAsIs ? new EbookSave(EbookSaveStatus.Unsupported, null, null) : await ConvertAsync(source, originalName, cancellationToken);
        }

        if (IsOtherComic(originalName))
        {
            return await RepackAsync(source, originalName, cancellationToken);
        }

        if (extension is not ".epub" and not ".pdf" and not ".cbz" and not ".mobi" and not ".azw3" and not ".azw")
        {
            return new EbookSave(EbookSaveStatus.Unsupported, null, null);
        }

        Directory.CreateDirectory(Root);
        var storedName = $"{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(Root, storedName);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // A copy cut off (the disk full, the request ended) leaves no part-file behind: nothing would ever name it.
        try
        {
            await using (var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaxBytes)
                    {
                        output.Close();
                        File.Delete(fullPath);
                        return new EbookSave(EbookSaveStatus.TooLarge, null, null);
                    }

                    fingerprint.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            // A file that can't be what its name says (cut short, or not that kind at all) goes again, said plainly,
            // rather than kept as an e-book the reader would fail to open.
            if (!LooksWhole(fullPath, extension))
            {
                File.Delete(fullPath);
                return new EbookSave(EbookSaveStatus.Damaged, null, null);
            }

            // The fingerprint is taken while the file streams in, so sync never has to read it again.
            var hash = Convert.ToHexString(fingerprint.GetHashAndReset()).ToLowerInvariant();
            await Fingerprint.WriteAsync(fullPath + Fingerprint.Extension, hash, cancellationToken);
        }
        catch
        {
            File.Delete(fullPath);
            File.Delete(fullPath + Fingerprint.Extension);
            throw;
        }

        var displayName = Path.GetFileName(originalName.Trim());
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = storedName;
        }

        if (displayName.Length > 200)
        {
            displayName = displayName[^200..];
        }

        return new EbookSave(EbookSaveStatus.Saved, storedName, displayName);
    }

    // A PDF carries its header within the first kilobyte; an EPUB or a CBZ is a zip that opens with something inside.
    internal static bool LooksWhole(string path, string extension)
    {
        try
        {
            // A Kindle book names itself BOOKMOBI sixty bytes in.
            if (extension is ".mobi" or ".azw3" or ".azw")
            {
                using var kindle = File.OpenRead(path);
                var start = new byte[68];
                return kindle.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) == start.Length && start.AsSpan(60, 8).SequenceEqual("BOOKMOBI"u8);
            }

            if (extension == ".pdf")
            {
                using var file = File.OpenRead(path);
                var head = new byte[1024];
                var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                return head.AsSpan(0, read).IndexOf("%PDF-"u8) >= 0;
            }

            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            return zip.Entries.Count > 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Comics in RAR, 7-Zip, or tar archives, which are repacked as CBZ, the zip every comic reader takes.
    public static bool IsOtherComic(string name) => Path.GetExtension(name).ToLowerInvariant() is ".cbr" or ".cb7" or ".cbt";

    private async Task<EbookSave> RepackAsync(Stream source, string originalName, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-repack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var input = Path.Combine(folder, "comic" + Path.GetExtension(originalName).ToLowerInvariant());
            await using (var output = File.Create(input))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if ((total += read) > MaxBytes)
                    {
                        return new EbookSave(EbookSaveStatus.TooLarge, null, null);
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            var zip = Path.Combine(folder, "comic.cbz");
            if (!ComicFile.Repack(input, zip))
            {
                return new EbookSave(EbookSaveStatus.Unsupported, null, null);
            }

            await using var repacked = File.OpenRead(zip);
            return await SaveAsync(repacked, Path.ChangeExtension(originalName, ".cbz"), cancellationToken);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private async Task<EbookSave> ConvertAsync(Stream source, string originalName, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-convert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var input = Path.Combine(folder, "book" + Path.GetExtension(originalName).ToLowerInvariant());
            await using (var output = File.Create(input))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                    {
                        return new EbookSave(EbookSaveStatus.TooLarge, null, null);
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            var epub = Path.Combine(folder, "book.epub");
            if (await Tools.RunAsync(Converter, [input, epub], TimeSpan.FromMinutes(3), cancellationToken) is null || !File.Exists(epub))
            {
                // Missing Calibre, a file it could not read, or one locked with DRM.
                return new EbookSave(EbookSaveStatus.NeedsConverter, null, null);
            }

            await using var converted = File.OpenRead(epub);
            return await SaveAsync(converted, Path.ChangeExtension(originalName, ".epub"), cancellationToken);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Whether Calibre's converter answers here, asked once for each place it is looked for.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<bool>> Answers = new();

    public Task<bool> CanConvertAsync() =>
        Answers.GetOrAdd(Converter, tool => Task.Run(async () => await Tools.RunAsync(tool, ["--version"], TimeSpan.FromSeconds(30), CancellationToken.None) is not null));

    // A stored e-book in another format, made by Calibre in a folder of its own, which the caller deletes with
    // DeleteMade once done. Null when Calibre can't read it (one locked with DRM, say) or isn't there.
    public async Task<string?> ConvertToAsync(string storedName, string extension, CancellationToken cancellationToken)
    {
        if (OpenPath(storedName) is not { } source)
        {
            return null;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"shelf-convert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var made = Path.Combine(folder, "book" + extension);
        if (await Tools.RunAsync(Converter, [source, made], TimeSpan.FromMinutes(5), cancellationToken) is not null && File.Exists(made) && new FileInfo(made).Length > 0)
        {
            return made;
        }

        DeleteMade(made);
        return null;
    }

    public static void DeleteMade(string? made)
    {
        if (made is not null && Path.GetDirectoryName(made) is { } folder && Path.GetFileName(folder).StartsWith("shelf-convert-", StringComparison.Ordinal) && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    public static string ContentType(string? storedName) => Path.GetExtension(storedName ?? "").ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".cbz" => "application/vnd.comicbook+zip",
        ".mobi" => "application/x-mobipocket-ebook",
        ".azw3" or ".azw" => "application/vnd.amazon.ebook",
        _ => "application/epub+zip",
    };

    // What Shelf's reader opens, and so what a book's main e-book can be.
    public static bool IsReadable(string? storedName) => IsEpub(storedName) || IsPdf(storedName) || IsComic(storedName);

    // The format a file is, in a word: EPUB, PDF, AZW3.
    public static string FormatOf(string? storedName) => Path.GetExtension(storedName ?? "").TrimStart('.').ToUpperInvariant();

    public string? OpenPath(string? storedName)
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
        if (!full.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(full))
        {
            return null;
        }

        return full;
    }

    public void Delete(string? storedName)
    {
        var full = OpenPath(storedName);
        if (full is null)
        {
            return;
        }

        try
        {
            File.Delete(full);
        }
        catch (IOException)
        {
        }

        Fingerprint.Forget(full + Fingerprint.Extension);
    }

    public async Task<string?> HashAsync(string? storedName, CancellationToken cancellationToken)
    {
        var path = OpenPath(storedName);
        if (path is null)
        {
            return null;
        }

        return await Fingerprint.ReadOrComputeAsync(path + Fingerprint.Extension, async () =>
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }, cancellationToken);
    }

    // The SHA-256 of the bytes as they are now, never the copy kept beside them: the file check asks whether they changed.
    public async Task<string?> HashNowAsync(string? storedName, CancellationToken cancellationToken)
    {
        if (OpenPath(storedName) is not { } path)
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    // Where an upload waits while the reader decides whether to keep a second copy of a file.
    public string HeldRoot => Path.Combine(Root, "held");

    public bool Hold(string storedName) => Move(OpenPath(storedName), HeldPath(storedName));

    public bool Release(string storedName) => Move(HeldPath(storedName), Path.Combine(Root, Path.GetFileName(storedName)));

    public void Forget(string storedName)
    {
        if (HeldPath(storedName) is { } path)
        {
            File.Delete(path);
            File.Delete(path + Fingerprint.Extension);
        }
    }

    private string? HeldPath(string storedName)
    {
        var name = Path.GetFileName(storedName);
        return name == storedName && name.Length > 0 && !name.Contains("..", StringComparison.Ordinal)
            ? Path.Combine(HeldRoot, name)
            : null;
    }

    private static bool Move(string? from, string? to)
    {
        if (from is null || to is null || !File.Exists(from))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
        if (File.Exists(from + Fingerprint.Extension))
        {
            File.Move(from + Fingerprint.Extension, to + Fingerprint.Extension, overwrite: true);
        }

        return true;
    }

    public static bool IsPdf(string? storedName) =>
        storedName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsComic(string? storedName) =>
        storedName?.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsEpub(string? storedName) =>
        storedName?.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) == true;
}

public enum EbookSaveStatus
{
    Saved,
    Unsupported,
    TooLarge,
    NeedsConverter,

    // Named as an EPUB, PDF, or CBZ, but cut short or not that kind of file.
    Damaged,
}

public sealed record EbookSave(EbookSaveStatus Status, string? StoredName, string? FileName);
