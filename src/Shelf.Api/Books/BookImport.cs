using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public enum ImportOutcome
{
    Added,
    AddedToExisting,
    AlreadyOnShelf,
    Unsupported,
    TooLarge,
    NeedsConverter,
}

public sealed record ImportedFile(string FileName, ImportOutcome Outcome, int? BookId = null, string? Title = null, int? SameAsId = null);

public sealed record ImportFile(string Name, Stream Content, long Length);

// Turns files into books: each e-book, each zip of tracks, and each run of audio files sharing an album becomes one.
// What the files say fills the catalog. A file already on another book is skipped unless both are wanted, and a
// book already on the shelf without that kind of file takes it instead of a second book being made.
public sealed class BookImport(EbookStore ebooks, AudioStore audio, CoverStore covers, IConfiguration configuration)
{
    public const string UnknownAuthor = "Unknown author";

    private static readonly HashSet<string> EbookExtensions = new(StringComparer.OrdinalIgnoreCase) { ".epub", ".pdf", ".cbz" };

    public static bool IsEbook(string name) => EbookExtensions.Contains(Path.GetExtension(name)) || EbookStore.IsKindle(name);

    public static bool IsAudio(string name) => Path.GetExtension(name).ToLowerInvariant() is ".mp3" or ".m4a" or ".m4b" or ".aac" or ".ogg" or ".opus" or ".wav" or ".flac";

    public static bool IsZip(string name) => Path.GetExtension(name).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    public async Task<List<ImportedFile>> ImportAsync(ShelfDb db, IReadOnlyList<ImportFile> files, bool keepBoth, CancellationToken cancellationToken)
    {
        var results = new List<ImportedFile>();
        foreach (var file in files.Where(file => IsEbook(file.Name)))
        {
            results.Add(await EbookAsync(db, file, keepBoth, null, cancellationToken));
        }

        foreach (var file in files.Where(file => IsZip(file.Name)))
        {
            results.Add(await AudioAsync(db, [file], Path.GetFileName(file.Name), null, keepBoth, cancellationToken));
        }

        var tracks = files.Where(file => IsAudio(file.Name)).ToList();
        if (tracks.Count > 0)
        {
            results.AddRange(await TracksAsync(db, tracks, keepBoth, cancellationToken));
        }

        results.AddRange(files
            .Where(file => !IsEbook(file.Name) && !IsZip(file.Name) && !IsAudio(file.Name))
            .Select(file => new ImportedFile(Path.GetFileName(file.Name), ImportOutcome.Unsupported)));
        return results;
    }

    // One e-book file. Details already known (from a catalog, say) take the place of what the file says.
    public async Task<ImportedFile> EbookAsync(ShelfDb db, ImportFile file, bool keepBoth, FileDetails? known, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(file.Name);
        var saved = await ebooks.SaveAsync(file.Content, file.Name, cancellationToken);
        if (saved.Status != EbookSaveStatus.Saved || saved.StoredName is null || saved.FileName is null)
        {
            return new ImportedFile(name, saved.Status switch
            {
                EbookSaveStatus.TooLarge => ImportOutcome.TooLarge,
                EbookSaveStatus.NeedsConverter => ImportOutcome.NeedsConverter,
                _ => ImportOutcome.Unsupported,
            });
        }

        if (!keepBoth && await Duplicates.FindEbookAsync(db, ebooks, 0, saved.StoredName, cancellationToken) is { } same)
        {
            ebooks.Delete(saved.StoredName);
            return new ImportedFile(name, ImportOutcome.AlreadyOnShelf, same.Id, same.Title, same.Id);
        }

        var path = ebooks.OpenPath(saved.StoredName)!;
        var details = known
            ?? (EbookStore.IsEpub(saved.StoredName) ? EpubFile.Details(path)
                : EbookStore.IsComic(saved.StoredName) ? ComicFile.Details(path)
                : await PdfDetails.ReadAsync(path, configuration["Ocr:PdfInfo"] ?? "pdfinfo", cancellationToken));
        var (book, existing) = await PlaceAsync(db, details, name, book => book.EbookStoredName is null, BookFormat.Ebook, cancellationToken);
        await EbookEndpoints.AttachAsync(db, ebooks, book, saved.StoredName, saved.FileName, cancellationToken);
        return new ImportedFile(name, existing ? ImportOutcome.AddedToExisting : ImportOutcome.Added, book.Id, book.Title);
    }

    // Loose tracks: grouped by the album their tags name, so one audiobook's files make one book.
    private async Task<List<ImportedFile>> TracksAsync(ShelfDb db, List<ImportFile> tracks, bool keepBoth, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var copies = new List<(ImportFile File, string Path, FileDetails? Details)>();
            foreach (var track in tracks)
            {
                var copy = Path.Combine(folder, $"{copies.Count:0000}{Path.GetExtension(track.Name).ToLowerInvariant()}");
                await using (var output = File.Create(copy))
                {
                    await track.Content.CopyToAsync(output, cancellationToken);
                }

                copies.Add((track, copy, AudioDetails.Read(copy)));
            }

            var results = new List<ImportedFile>();
            foreach (var group in copies.GroupBy(copy => copy.Details?.Album is { } album ? "album:" + album : "file:" + copy.Path))
            {
                var members = group.ToList();
                var streams = members.Select(member => new ImportFile(member.File.Name, File.OpenRead(member.Path), new FileInfo(member.Path).Length)).ToList();
                try
                {
                    var label = members.Count == 1 ? Path.GetFileName(members[0].File.Name) : $"{members.Count} tracks of {members[0].Details?.Album}";
                    results.Add(await AudioAsync(db, streams, label, members[0].Details, keepBoth, cancellationToken));
                }
                finally
                {
                    foreach (var stream in streams)
                    {
                        await stream.Content.DisposeAsync();
                    }
                }
            }

            return results;
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    public async Task<ImportedFile> AudioAsync(ShelfDb db, IReadOnlyList<ImportFile> files, string label, FileDetails? known, bool keepBoth, CancellationToken cancellationToken)
    {
        var saved = await audio.SaveAsync(files.Select(file => new AudioUpload(file.Name, file.Content, file.Length)).ToList(), cancellationToken);
        if (saved.Status != AudioSaveStatus.Saved || saved.StoredName is null || saved.FileName is null)
        {
            return new ImportedFile(label, saved.Status == AudioSaveStatus.TooLarge ? ImportOutcome.TooLarge : ImportOutcome.Unsupported);
        }

        if (!keepBoth && await Duplicates.FindAudioAsync(db, audio, 0, saved.StoredName, cancellationToken) is { } same)
        {
            audio.Delete(saved.StoredName);
            return new ImportedFile(label, ImportOutcome.AlreadyOnShelf, same.Id, same.Title, same.Id);
        }

        // A zip's tracks are only read once it is unpacked, so its first track speaks for it.
        var details = known ?? (audio.TrackPath(saved.StoredName, 0) is { } first ? AudioDetails.Read(first) : null);
        var fallback = files.Count == 1 ? files[0].Name : label;
        var (book, existing) = await PlaceAsync(db, details, fallback, book => book.AudioStoredName is null, BookFormat.Audiobook, cancellationToken);
        await AudioEndpoints.AttachAsync(db, audio, covers, book, saved.StoredName, saved.FileName, cancellationToken);
        return new ImportedFile(label, existing ? ImportOutcome.AddedToExisting : ImportOutcome.Added, book.Id, book.Title);
    }

    // The book a file belongs on: one already on the shelf that matches and lacks that kind of file, or a new one.
    private static async Task<(Book Book, bool Existing)> PlaceAsync(
        ShelfDb db, FileDetails? details, string fileName, Func<Book, bool> lacksFile, BookFormat format, CancellationToken cancellationToken)
    {
        var title = Fit(details?.Title, 200) ?? TitleFromName(fileName);
        var author = Fit(details?.Author, 200) ?? UnknownAuthor;
        var isbn = BookRules.NormalizeIsbn(details?.Isbn);
        var lowered = title.ToLower(CultureInfo.InvariantCulture);
        var candidates = await db.Books
            .Where(book => book.Title.ToLower() == lowered || (isbn != null && book.Isbn == isbn))
            .ToListAsync(cancellationToken);
        var match = author == UnknownAuthor
            ? null
            : candidates.FirstOrDefault(book => lacksFile(book) && BookRules.IsSameCopy(isbn, title, author, book.Isbn, book.Title, book.Author));
        if (match is not null)
        {
            return (match, true);
        }

        var created = new Book
        {
            Title = title,
            Author = author,
            Isbn = isbn,
            Publisher = Fit(details?.Publisher, 200),
            Year = details?.Year is >= 1000 and <= 2100 ? details.Year : null,
            Language = LanguageName(details?.Language),
            Format = format,
        };
        db.Books.Add(created);
        await db.SaveChangesAsync(cancellationToken);
        return (created, false);
    }

    // "the_left_hand_of_darkness.epub" becomes "the left hand of darkness".
    public static string TitleFromName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Replace('.', ' ').Trim();
        return Fit(stem, 200) ?? "Untitled";
    }

    // "en" or "en-GB" becomes "English"; anything else is kept as written.
    private static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(code.Trim());
            var name = culture.IsNeutralCulture ? culture.EnglishName : culture.Parent.EnglishName;
            return Fit(string.IsNullOrWhiteSpace(name) || name.StartsWith("Unknown", StringComparison.Ordinal) ? code : name, 40);
        }
        catch (CultureNotFoundException)
        {
            return Fit(code, 40);
        }
    }

    private static string? Fit(string? value, int length)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > length ? trimmed[..length].Trim() : trimmed;
    }
}
