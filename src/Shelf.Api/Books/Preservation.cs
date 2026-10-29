using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record ManifestFile(string Path, long Bytes, string Sha256);

public sealed record Manifest(string Format, int Version, string ShelfVersion, DateTimeOffset CreatedAt, ManifestFile[] Files);

// How a download held up against its manifest: the files checked, and those missing or not as they were written.
public sealed record ArchiveCheck(bool HasManifest, int Checked, string[] Damaged, string[] Missing)
{
    public bool Whole => HasManifest && Damaged.Length == 0 && Missing.Length == 0;
}

// A stored file in a format that may not be readable in years to come, and why.
public sealed record FormatRisk(int BookId, string Title, string File, string Why);

// Every file written into a download, with its size and SHA-256, so the download can be shown whole years later by
// any tool that can read a zip and a hash.
public sealed class ArchiveManifest
{
    public const string EntryName = "manifest.json";
    public const string SchemaName = "SCHEMA.md";
    private readonly List<ManifestFile> _files = [];

    public IReadOnlyList<ManifestFile> Files => _files;

    public Stream Create(ZipArchive zip, string name, CompressionLevel level = CompressionLevel.Optimal) =>
        new Recording(this, name, zip.CreateEntry(name, level).Open());

    public async Task CopyAsync(ZipArchive zip, string path, string name, CancellationToken cancellationToken = default)
    {
        await using var source = File.OpenRead(path);
        await using var target = Create(zip, name, CompressionLevel.NoCompression);
        await source.CopyToAsync(target, cancellationToken);
    }

    public async Task FinishAsync(ZipArchive zip, CancellationToken cancellationToken = default)
    {
        await using (var schema = Create(zip, SchemaName))
        {
            await schema.WriteAsync(Encoding.UTF8.GetBytes(Preservation.Schema), cancellationToken);
        }

        var manifest = new Manifest("shelf-export", 1, typeof(ArchiveManifest).Assembly.GetName().Version?.ToString(3) ?? "", DateTimeOffset.UtcNow, [.. _files]);
        await using var stream = zip.CreateEntry(EntryName).Open();
        await JsonSerializer.SerializeAsync(stream, manifest, Preservation.Json, cancellationToken);
    }

    private sealed class Recording(ArchiveManifest manifest, string name, Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _bytes;
        private bool _done;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes;
        public override long Position { get => _bytes; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            _bytes += buffer.Length;
            inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            _bytes += buffer.Length;
            await inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Done();
                inner.Dispose();
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Done();
            await inner.DisposeAsync();
            _hash.Dispose();
            await base.DisposeAsync();
        }

        private void Done()
        {
            if (!_done)
            {
                _done = true;
                manifest._files.Add(new ManifestFile(name, _bytes, Convert.ToHexStringLower(_hash.GetHashAndReset())));
            }
        }
    }
}

public static class Preservation
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // A download held up against its manifest, file by file.
    public static ArchiveCheck Check(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(ArchiveManifest.EntryName);
        if (entry is null)
        {
            return new ArchiveCheck(false, 0, [], []);
        }

        Manifest? manifest;
        try
        {
            using var stream = entry.Open();
            manifest = JsonSerializer.Deserialize<Manifest>(stream, Json);
        }
        catch (JsonException)
        {
            return new ArchiveCheck(true, 0, [ArchiveManifest.EntryName], []);
        }

        var (damaged, missing, checkedFiles) = (new List<string>(), new List<string>(), 0);
        foreach (var file in manifest?.Files ?? [])
        {
            var found = zip.GetEntry(file.Path);
            if (found is null)
            {
                missing.Add(file.Path);
                continue;
            }

            checkedFiles++;
            try
            {
                using var stream = found.Open();
                var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (hash != file.Sha256 || found.Length != file.Bytes)
                {
                    damaged.Add(file.Path);
                }
            }
            catch (InvalidDataException)
            {
                damaged.Add(file.Path);
            }
        }

        return new ArchiveCheck(true, checkedFiles, [.. damaged], [.. missing]);
    }

    // Formats a reader may struggle to open in years to come: closed ones, ones the makers have left behind, and
    // files locked to one shop's readers.
    public static string? Risk(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mobi" or ".prc" => T("Mobipocket is no longer made; Kindles take EPUB now. Calibre can turn it into an EPUB."),
        ".azw" or ".azw3" or ".kfx" => T("A Kindle format that only Amazon's readers open, often locked to one account. Keep an EPUB beside it if you can."),
        ".lit" => T("Microsoft Reader was abandoned in 2012, and little opens .lit now. Calibre can turn it into an EPUB."),
        ".lrf" or ".lrx" => T("Sony's old reader format; Calibre can turn it into an EPUB."),
        ".pdb" => T("An old Palm format few readers open now; Calibre can turn it into an EPUB."),
        ".cbr" => T("A comic in a RAR archive, which needs a program not every system has. A CBZ holds the same pages in a plain zip."),
        ".aa" or ".aax" => T("Audible's own format, locked to an account. An M4B made from it keeps the chapters and plays anywhere."),
        ".wma" => T("Windows Media Audio plays in fewer and fewer places; M4B or MP3 would last better."),
        ".ra" or ".rm" => T("RealAudio is all but gone; MP3 or M4B would last better."),
        _ => null,
    };

    // An EPUB whose words are locked by a shop: encrypted with something other than the fonts' own obfuscation.
    public static bool Locked(string epubPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(epubPath);
            if (zip.GetEntry("META-INF/rights.xml") is not null)
            {
                return true;
            }

            if (zip.GetEntry("META-INF/encryption.xml") is not { } entry)
            {
                return false;
            }

            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();
            var algorithms = System.Text.RegularExpressions.Regex.Matches(text, "Algorithm=\"([^\"]+)\"").Select(match => match.Groups[1].Value);
            return algorithms.Any(algorithm => !algorithm.Contains("obfuscation", StringComparison.OrdinalIgnoreCase) && !algorithm.Contains("font", StringComparison.OrdinalIgnoreCase)
                && !algorithm.EndsWith("xmldsig#sha1", StringComparison.OrdinalIgnoreCase) && !algorithm.Contains("deflate", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }
    }

    public static async Task<FormatRisk[]> RisksAsync(ShelfDb db, EbookStore ebooks, CancellationToken cancellationToken = default)
    {
        var books = await db.Books.AsNoTracking().Where(book => book.EbookFileName != null || book.AudioFileName != null)
            .Select(book => new { book.Id, book.Title, book.EbookFileName, book.EbookStoredName, book.AudioFileName }).ToListAsync(cancellationToken);
        var risks = new List<FormatRisk>();
        foreach (var book in books)
        {
            if (book.EbookFileName is { } ebook)
            {
                if (Risk(ebook) is { } why)
                {
                    risks.Add(new FormatRisk(book.Id, book.Title, ebook, why));
                }
                else if (ebook.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) && ebooks.OpenPath(book.EbookStoredName) is { } path && Locked(path))
                {
                    risks.Add(new FormatRisk(book.Id, book.Title, ebook, T("Locked by the shop it came from: only that shop's readers can open it, and only while the shop lets them.")));
                }
            }

            if (book.AudioFileName is { } recording && Risk(recording) is { } heard)
            {
                risks.Add(new FormatRisk(book.Id, book.Title, recording, heard));
            }
        }

        return [.. risks.OrderBy(risk => risk.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    public const string Schema = """
        # What is in this download

        Written by Shelf so the download can be read, checked, and brought back without Shelf. Text is UTF-8; JSON keys
        are camelCase; dates are ISO 8601 (`2024-03-14`), moments ISO 8601 with their offset.

        ## Checking it

        `manifest.json` names every other file with its size in bytes and its SHA-256. On most systems
        `sha256sum books/0001/ebook/*` (or `Get-FileHash` in PowerShell) gives a hash to hold against it. The manifest
        does not list itself.

        ## shelf.json

        An object:

        - `yearlyGoal` — books meant to be read each year, 0 for none.
        - `books` — an array of books, in the order of the folders under `books/` (the first is `books/0001/`).
        - `journal` — entries: `date`, `text`, and `book`, the index of a book in `books` (from 0), or null.
        - `links` — connections between books: `from` and `to` (indexes in `books`), `kind`, and `note`.
        - `goals` — goals of the reader's own: `year`, `kind`, `name`, `target`.

        Each book has its catalog details (`title`, `subtitle`, `author`, `isbn`, `year`, `pages`, `publisher`,
        `language`, `format`, `series`, `seriesNumber`, `translator`, `narrator`, `originalTitle`, `coverUrl`), its
        reading (`status`: Want, Reading, Finished, or Abandoned; `rating` 1–5; `loved`; `startedOn`; `finishedOn`;
        `currentPage`; `review`; `notes`; `tags`; `sessions`; `quotes`; `highlights`; `pastReads`), and the copy itself
        (`location`, `acquiredOn`, `acquisition`, `condition`, `pricePaid`, `inscription`, `previousOwners`, `signed`,
        `recommendedBy`, `loanedTo`, `loanedOn`, `dueOn`). `life`, when present, says whether and how the copy left
        (`leftOn`, `leftHow`, `leftTo`, `soldFor`), its place in the reading queue (`queueOrder`, `heldUntil`,
        `holdNote`), and where it usually lives while away (`homeLocation`). `ebookFileName` and `audioFileName` name the
        files kept under the book's folder.

        ## The other files

        - `books.csv` — the books as a spreadsheet; `Number` counts from 1 in the order of `books`.
        - `collections.json`, `playlists.json`, `listening-sessions.json`, `loans.json`, `borrowed-highlights.json` — described in `README.md`.
        - `books/0001/ebook/`, `books/0001/audio/`, `books/0001/cover.*` — each book's own files.
        """;
}
