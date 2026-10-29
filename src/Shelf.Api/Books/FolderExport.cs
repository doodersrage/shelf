using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// The reader's books as folders anyone can find their way around, laid out as Calibre lays out its library:
// Author/Title (id)/, with each e-book format, the cover, the audiobook's tracks, and a metadata.opf saying what the
// book is. Calibre reads the OPF when books are added from folders, one book to a folder; so does any program that
// knows OPF. Notes, reviews, and reading history are in the full backup, not here.
public static class FolderExport
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    public static async Task WriteAsync(ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, string zipPath, CancellationToken cancellationToken = default)
    {
        var books = await db.Books.AsNoTracking().Include(book => book.Tags).OrderBy(book => book.Id).ToListAsync(cancellationToken);
        var formats = (await db.EbookFormats.AsNoTracking().ToListAsync(cancellationToken)).ToLookup(format => format.BookId);
        await using var output = File.Create(zipPath);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var book in books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = $"{Safe(book.Author, 80)}/{Safe(book.Title, 100)} ({book.Id})/";
            var name = Safe($"{book.Title} - {book.Author}", 120);
            var cover = covers.OpenPath(book.CoverImage);
            Write(zip, folder + "metadata.opf", Package(book, cover is null ? null : "cover" + Path.GetExtension(cover).ToLowerInvariant()));
            if (cover is not null)
            {
                zip.CreateEntryFromFile(cover, folder + "cover" + Path.GetExtension(cover).ToLowerInvariant(), CompressionLevel.NoCompression);
            }

            foreach (var stored in new[] { book.EbookStoredName }.Concat(formats[book.Id].Select(format => format.StoredName)))
            {
                if (ebooks.OpenPath(stored) is { } path)
                {
                    zip.CreateEntryFromFile(path, folder + name + Path.GetExtension(path).ToLowerInvariant(), CompressionLevel.NoCompression);
                }
            }

            foreach (var track in audio.Tracks(book.AudioStoredName))
            {
                if (audio.TrackPath(book.AudioStoredName, track.Index) is { } trackPath)
                {
                    zip.CreateEntryFromFile(trackPath, $"{folder}audiobook/{Path.GetFileName(trackPath)}", CompressionLevel.NoCompression);
                }
            }
        }
    }

    // The book as an OPF 2.0 package, with Calibre's own fields for its series and rating.
    public static XDocument Package(Book book, string? cover)
    {
        var metadata = new XElement(Opf + "metadata",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XAttribute(XNamespace.Xmlns + "opf", Opf),
            new XElement(Dc + "identifier", new XAttribute(Opf + "scheme", "shelf"), new XAttribute("id", "shelf-id"), book.Id.ToString(CultureInfo.InvariantCulture)),
            new XElement(Dc + "title", book.Title));
        foreach (var author in AbsJson.Split(book.Author).Where(name => name != BookImport.UnknownAuthor))
        {
            metadata.Add(new XElement(Dc + "creator", new XAttribute(Opf + "role", "aut"), author));
        }

        void Add(XName name, string? value, params object[] attributes)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                metadata.Add(new XElement(name, attributes, value.Trim()));
            }
        }

        Add(Dc + "publisher", book.Publisher);
        Add(Dc + "date", book.Year?.ToString("0000", CultureInfo.InvariantCulture));
        Add(Dc + "language", EpubDetails.LanguageCode(book.Language) ?? book.Language);
        Add(Dc + "identifier", book.Isbn, new XAttribute(Opf + "scheme", "ISBN"));
        foreach (var tag in book.Tags.Select(tag => tag.Name).Order(StringComparer.Ordinal))
        {
            Add(Dc + "subject", tag);
        }

        if (!string.IsNullOrWhiteSpace(book.Series))
        {
            metadata.Add(new XElement(Opf + "meta", new XAttribute("name", "calibre:series"), new XAttribute("content", book.Series)));
            if (book.SeriesNumber is int number)
            {
                metadata.Add(new XElement(Opf + "meta", new XAttribute("name", "calibre:series_index"), new XAttribute("content", number.ToString(CultureInfo.InvariantCulture))));
            }
        }

        // Calibre counts stars in halves, out of ten.
        if (book.Rating is int rating)
        {
            metadata.Add(new XElement(Opf + "meta", new XAttribute("name", "calibre:rating"), new XAttribute("content", (rating * 2).ToString(CultureInfo.InvariantCulture))));
        }

        var package = new XElement(Opf + "package", new XAttribute("version", "2.0"), new XAttribute("unique-identifier", "shelf-id"), metadata);
        if (cover is not null)
        {
            package.Add(new XElement(Opf + "guide", new XElement(Opf + "reference", new XAttribute("type", "cover"), new XAttribute("title", "Cover"), new XAttribute("href", cover))));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), package);
    }

    private static void Write(ZipArchive zip, string name, XDocument document)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        document.Save(stream);
    }

    // A name any file system takes: no slashes or other marks it refuses, not too long, and never empty.
    private static string Safe(string text, int max)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string(text.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray()).Trim().Trim('.');
        clean = clean.Length > max ? clean[..max].Trim() : clean;
        return clean.Length == 0 ? "_" : clean;
    }
}
