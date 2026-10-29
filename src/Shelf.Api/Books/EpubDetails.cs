using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Shelf.Api.Books;

// What Shelf knows of a book, as an EPUB's own package can carry it: written into a copy of the file on its way out,
// so the book shows the shelf's title, authors, series, and cover in any app or device it goes to. Notes, reviews,
// ratings, and tags are the reader's own and are never written.
public sealed record EpubDetailsToWrite(
    string Title,
    IReadOnlyList<string> Authors,
    string? Series,
    int? SeriesNumber,
    string? Publisher,
    string? Language,
    int? Year,
    string? Isbn,
    string? CoverPath)
{
    public static EpubDetailsToWrite For(Book book, CoverStore covers) => new(
        book.Title,
        AbsJson.Split(book.Author).Where(name => name != BookImport.UnknownAuthor).ToList(),
        book.Series,
        book.SeriesNumber,
        book.Publisher,
        EpubDetails.LanguageCode(book.Language),
        book.Year,
        book.Isbn,
        covers.OpenPath(book.CoverImage));
}

public static class EpubDetails
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    // False when the EPUB could not be read or its package not found; the caller sends the file as it is.
    public static bool Write(string epub, string target, EpubDetailsToWrite details)
    {
        var partial = target + ".part";
        try
        {
            using var source = ZipFile.OpenRead(epub);
            var opfPath = PackagePath(source);
            if (opfPath is null || source.GetEntry(opfPath) is not { } opfEntry)
            {
                return false;
            }

            XDocument package;
            using (var stream = opfEntry.Open())
            {
                package = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            }

            var folder = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
            var cover = details.CoverPath is { } coverPath && File.Exists(coverPath) ? coverPath : null;
            var coverEntry = cover is null ? null : folder + "shelf-cover" + Path.GetExtension(cover).ToLowerInvariant();
            if (!Apply(package, details, cover is null ? null : (coverEntry![folder.Length..], CoverStore.ContentType(cover))))
            {
                return false;
            }

            using (var file = File.Create(partial))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                // The mimetype comes first and unpacked, as the EPUB format asks.
                if (source.GetEntry("mimetype") is { } mimetype)
                {
                    Copy(mimetype, zip.CreateEntry("mimetype", CompressionLevel.NoCompression));
                }

                foreach (var entry in source.Entries.Where(entry => entry.FullName != "mimetype" && !entry.FullName.EndsWith('/') && entry.FullName != coverEntry))
                {
                    var made = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    if (entry.FullName == opfPath)
                    {
                        using var output = made.Open();
                        using var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
                        package.Save(writer);
                    }
                    else
                    {
                        Copy(entry, made);
                    }
                }

                if (cover is not null)
                {
                    using var input = File.OpenRead(cover);
                    using var output = zip.CreateEntry(coverEntry!, CompressionLevel.NoCompression).Open();
                    input.CopyTo(output);
                }
            }

            File.Move(partial, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or UnauthorizedAccessException or InvalidOperationException)
        {
            try
            {
                File.Delete(partial);
            }
            catch (IOException)
            {
            }

            return false;
        }
    }

    // The package's metadata set to the details: each field Shelf has replaces what the file said; one it lacks
    // leaves the file's own. Returns false for a package with no metadata to write into.
    public static bool Apply(XDocument package, EpubDetailsToWrite details, (string Href, string MediaType)? cover = null)
    {
        var root = package.Root;
        var metadata = root?.Element(Opf + "metadata");
        if (root is null || metadata is null)
        {
            return false;
        }

        var three = ((string?)root.Attribute("version"))?.StartsWith('3') == true;
        var uniqueId = (string?)root.Attribute("unique-identifier");

        // The title: the first one kept, with any refinements, and set; any others (a subtitle, say) go.
        var titles = metadata.Elements(Dc + "title").ToList();
        if (titles.Count == 0)
        {
            metadata.AddFirst(new XElement(Dc + "title", details.Title));
        }
        else
        {
            titles[0].Value = details.Title;
            foreach (var other in titles.Skip(1))
            {
                Remove(metadata, other);
            }
        }

        if (details.Authors.Count > 0)
        {
            var at = metadata.Elements(Dc + "creator").FirstOrDefault()?.ElementsBeforeSelf().LastOrDefault() ?? metadata.Elements(Dc + "title").Last();
            foreach (var creator in metadata.Elements(Dc + "creator").ToList())
            {
                Remove(metadata, creator);
            }

            var made = new List<XNode>();
            for (var i = 0; i < details.Authors.Count; i++)
            {
                var id = $"shelf-creator-{i + 1}";
                var creator = new XElement(Dc + "creator", new XAttribute("id", id), details.Authors[i]);
                if (three)
                {
                    made.Add(creator);
                    made.Add(new XElement(Opf + "meta", new XAttribute("refines", "#" + id), new XAttribute("property", "role"), new XAttribute("scheme", "marc:relators"), "aut"));
                }
                else
                {
                    creator.Add(new XAttribute(Opf + "role", "aut"));
                    made.Add(creator);
                }
            }

            at.AddAfterSelf(made);
        }

        Replace(metadata, Dc + "publisher", details.Publisher);
        Replace(metadata, Dc + "language", details.Language);

        if (details.Year is int year and > 0 and < 10000)
        {
            // The date it was published, and not the dates an EPUB 2 file gives for other events.
            foreach (var date in metadata.Elements(Dc + "date").Where(date => (string?)date.Attribute(Opf + "event") is null or "publication").ToList())
            {
                Remove(metadata, date);
            }

            metadata.Add(new XElement(Dc + "date", year.ToString("0000", CultureInfo.InvariantCulture)));
        }

        if (Isbn(details.Isbn) is { } isbn)
        {
            var value = three ? "urn:isbn:" + isbn : isbn;
            var found = false;
            foreach (var identifier in metadata.Elements(Dc + "identifier").Where(LooksLikeIsbn).ToList())
            {
                // The book's own identifier stays, as readers know the book by it; it only takes the ISBN.
                if (uniqueId is not null && (string?)identifier.Attribute("id") == uniqueId)
                {
                    identifier.Value = value;
                    found = true;
                }
                else
                {
                    Remove(metadata, identifier);
                }
            }

            if (!found)
            {
                var identifier = new XElement(Dc + "identifier", value);
                if (!three)
                {
                    identifier.Add(new XAttribute(Opf + "scheme", "ISBN"));
                }

                metadata.Add(identifier);
            }
        }

        if (!string.IsNullOrWhiteSpace(details.Series))
        {
            foreach (var meta in metadata.Elements(Opf + "meta").Where(meta => (string?)meta.Attribute("name") is "calibre:series" or "calibre:series_index" || (string?)meta.Attribute("property") == "belongs-to-collection").ToList())
            {
                Remove(metadata, meta);
            }

            // Calibre's way, which Kobo, KOReader, and most apps read; and EPUB 3's own.
            metadata.Add(new XElement(Opf + "meta", new XAttribute("name", "calibre:series"), new XAttribute("content", details.Series)));
            if (details.SeriesNumber is int number)
            {
                metadata.Add(new XElement(Opf + "meta", new XAttribute("name", "calibre:series_index"), new XAttribute("content", number.ToString(CultureInfo.InvariantCulture))));
            }

            if (three)
            {
                metadata.Add(new XElement(Opf + "meta", new XAttribute("property", "belongs-to-collection"), new XAttribute("id", "shelf-series"), details.Series));
                metadata.Add(new XElement(Opf + "meta", new XAttribute("refines", "#shelf-series"), new XAttribute("property", "collection-type"), "series"));
                if (details.SeriesNumber is int position)
                {
                    metadata.Add(new XElement(Opf + "meta", new XAttribute("refines", "#shelf-series"), new XAttribute("property", "group-position"), position.ToString(CultureInfo.InvariantCulture)));
                }
            }
        }

        if (cover is { } picture && root.Element(Opf + "manifest") is { } manifest)
        {
            // The shelf's cover as a picture of its own; the file's cover page, if it has one, is left as it was.
            foreach (var item in manifest.Elements(Opf + "item"))
            {
                if ((string?)item.Attribute("properties") is { } properties && properties.Split(' ').Contains("cover-image"))
                {
                    var rest = string.Join(' ', properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(property => property != "cover-image"));
                    item.SetAttributeValue("properties", rest.Length > 0 ? rest : null);
                }
            }

            manifest.Elements(Opf + "item").Where(item => (string?)item.Attribute("id") == "shelf-cover").Remove();
            var added = new XElement(Opf + "item", new XAttribute("id", "shelf-cover"), new XAttribute("href", picture.Href), new XAttribute("media-type", picture.MediaType));
            if (three)
            {
                added.Add(new XAttribute("properties", "cover-image"));
            }

            manifest.Add(added);
            metadata.Elements(Opf + "meta").Where(meta => (string?)meta.Attribute("name") == "cover").Remove();
            metadata.Add(new XElement(Opf + "meta", new XAttribute("name", "cover"), new XAttribute("content", "shelf-cover")));
        }

        return true;
    }

    // "English", "en", or "en-GB" as the code an EPUB wants; null for a language this can't place.
    public static string? LanguageCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var text = language.Trim();
        var match = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(culture =>
            culture.Name.Length > 0 && (string.Equals(culture.EnglishName, text, StringComparison.OrdinalIgnoreCase) || string.Equals(culture.NativeName, text, StringComparison.OrdinalIgnoreCase)));
        if (match is not null)
        {
            return match.Name;
        }

        // A code as written: two or three letters, and a region or script after.
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$"))
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(text);
            return culture.Name.Length > 0 && !culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal) ? culture.Name : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    // Names what would be written, so a copy made once is used until the book or its file changes.
    public static string Fingerprint(EpubDetailsToWrite details, string epub)
    {
        var cover = details.CoverPath is { } path && File.Exists(path) ? $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}" : "";
        var text = JsonSerializer.Serialize(details with { CoverPath = cover }) + "|" + File.GetLastWriteTimeUtc(epub).Ticks + "|1";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    private static string? Isbn(string? isbn)
    {
        var digits = new string((isbn ?? "").Where(character => char.IsDigit(character) || character is 'X' or 'x').ToArray()).ToUpperInvariant();
        return digits.Length is 10 or 13 ? digits : null;
    }

    private static bool LooksLikeIsbn(XElement identifier)
    {
        var scheme = (string?)identifier.Attribute(Opf + "scheme");
        var value = identifier.Value.Trim();
        return string.Equals(scheme, "ISBN", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("isbn:", StringComparison.OrdinalIgnoreCase);
    }

    // A field Shelf has replaces all of the file's; one it lacks leaves them.
    private static void Replace(XElement metadata, XName name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var existing = metadata.Elements(name).ToList();
        var after = existing.FirstOrDefault()?.ElementsBeforeSelf().LastOrDefault();
        foreach (var element in existing)
        {
            Remove(metadata, element);
        }

        var added = new XElement(name, value.Trim());
        if (after is not null)
        {
            after.AddAfterSelf(added);
        }
        else
        {
            metadata.Add(added);
        }
    }

    // An element and the EPUB 3 refinements that point at it.
    private static void Remove(XElement metadata, XElement element)
    {
        if ((string?)element.Attribute("id") is { } id)
        {
            metadata.Elements(Opf + "meta").Where(meta => (string?)meta.Attribute("refines") == "#" + id).Remove();
        }

        element.Remove();
    }

    private static void Copy(ZipArchiveEntry from, ZipArchiveEntry to)
    {
        using var input = from.Open();
        using var output = to.Open();
        input.CopyTo(output);
    }

    private static string? PackagePath(ZipArchive zip)
    {
        if (zip.GetEntry("META-INF/container.xml") is not { } container)
        {
            return null;
        }

        using var stream = container.Open();
        var document = XDocument.Load(stream);
        return document.Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
    }
}

// Copies of the shelf's EPUBs with its details written in, kept beside them so each is made once per change.
public sealed class EpubDetailsStore(EbookStore ebooks, CoverStore covers, ILogger<EpubDetailsStore> logger)
{
    private readonly SemaphoreSlim making = new(1, 1);

    private string Root => Path.Combine(ebooks.Root, "with-details");

    // The copy for a book's EPUB, made now if the details changed; the stored file itself when it isn't an EPUB or
    // can't be written into; null when there is no file.
    public async Task<string?> PathForAsync(Book book, CancellationToken cancellationToken = default)
    {
        if (ebooks.OpenPath(book.EbookStoredName) is not { } original)
        {
            return null;
        }

        if (!EbookStore.IsEpub(book.EbookStoredName))
        {
            return original;
        }

        var details = EpubDetailsToWrite.For(book, covers);
        var stem = Path.GetFileNameWithoutExtension(book.EbookStoredName)!;
        var path = Path.Combine(Root, $"{stem}.{EpubDetails.Fingerprint(details, original)}.epub");
        if (File.Exists(path))
        {
            return path;
        }

        await making.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
            {
                return path;
            }

            Directory.CreateDirectory(Root);
            if (!await Task.Run(() => EpubDetails.Write(original, path, details), cancellationToken))
            {
                logger.LogInformation("Could not write the details into {Book}; it goes as it is.", book.EbookStoredName);
                return original;
            }

            // Copies made for the book's details before these.
            foreach (var old in Directory.GetFiles(Root, stem + ".*.epub").Where(file => file != path))
            {
                File.Delete(old);
            }

            return path;
        }
        finally
        {
            making.Release();
        }
    }

    // Copies whose EPUB is gone.
    public void Sweep(IReadOnlySet<string> storedNames)
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var stems = storedNames.Select(Path.GetFileNameWithoutExtension).ToHashSet();
        foreach (var file in Directory.GetFiles(Root, "*.epub"))
        {
            var name = Path.GetFileName(file);
            var stem = name[..name.IndexOf('.')];
            if (!stems.Contains(stem))
            {
                File.Delete(file);
            }
        }
    }
}
