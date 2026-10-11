using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Shelf.Api.Books;

// A comic book archive (.cbz): page images in a zip, read in name order, with an optional ComicInfo.xml.
public static partial class ComicFile
{
    private const long MaxPageBytes = 40L * 1024 * 1024;

    // Pages and ComicInfo.xml from a RAR, 7-Zip, or tar archive into a zip, in the same order; false when none could be read.
    public static bool Repack(string archive, string cbz)
    {
        try
        {
            using var source = SharpCompress.Archives.ArchiveFactory.OpenArchive(archive);
            var wanted = source.Entries
                .Where(entry => !entry.IsDirectory && entry.Key is { } key && (ImageType(key) is not null || Path.GetFileName(key).Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase))
                    && !key.Replace('\\', '/').Split('/').Any(part => part.StartsWith('.') || part == "__MACOSX"))
                .ToList();
            if (!wanted.Any(entry => ImageType(entry.Key!) is not null))
            {
                return false;
            }

            using var file = File.Create(cbz);
            using var target = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var entry in wanted)
            {
                // Images are packed already; storing them saves time and nothing else.
                var made = target.CreateEntry(entry.Key!.Replace('\\', '/'), ImageType(entry.Key) is null ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                using var input = entry.OpenEntryStream();
                using var output = made.Open();
                input.CopyTo(output);
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or SharpCompress.Common.ArchiveException or SharpCompress.Common.InvalidFormatException or NotSupportedException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> Pages(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries
                .Where(entry => entry.Length > 0 && ImageType(entry.FullName) is not null && !entry.FullName.Split('/').Any(part => part.StartsWith('.') || part == "__MACOSX"))
                .Select(entry => entry.FullName)
                .OrderBy(name => NaturalKey(name), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return [];
        }
    }

    public static (byte[] Bytes, string ContentType)? Page(string? path, int index)
    {
        var pages = Pages(path);
        if (index < 0 || index >= pages.Count)
        {
            return null;
        }

        try
        {
            using var zip = ZipFile.OpenRead(path!);
            var entry = zip.GetEntry(pages[index]);
            if (entry is null || entry.Length > MaxPageBytes)
            {
                return null;
            }

            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return (copy.ToArray(), ImageType(entry.FullName)!);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    // ComicInfo.xml, the tag file comic tools write: Title (or Series and Number) and Writer.
    public static FileDetails? Details(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var info = zip.Entries.FirstOrDefault(entry => entry.Name.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
            if (info is null)
            {
                return null;
            }

            using var stream = info.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var root = XDocument.Load(reader).Root;
            string? Field(string name) => root?.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } value ? value : null;
            var series = Field("Series");
            var number = Field("Number");
            var title = Field("Title") ?? (series is null ? null : number is null ? series : $"{series} #{number}");
            int? year = int.TryParse(Field("Year"), out var parsed) ? parsed : null;
            return new FileDetails(title, Field("Writer"), Publisher: Field("Publisher"), Year: year, Language: Field("LanguageISO"));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException)
        {
            return null;
        }
    }

    // Whether ComicInfo.xml says the comic reads right to left, as manga does.
    public static bool IsRightToLeft(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var zip = ZipFile.OpenRead(path);
            var info = zip.Entries.FirstOrDefault(entry => entry.Name.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
            if (info is null)
            {
                return false;
            }

            using var stream = info.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader).Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "Manga")?.Value.Trim() == "YesAndRightToLeft";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException)
        {
            return false;
        }
    }

    // The pages shown together from a page: the cover alone, then two at a time (2 and 3, 4 and 5, …) in a spread.
    public static (int First, int Count) Spread(int page, int pages, bool spread)
    {
        if (!spread || page <= 0)
        {
            return (Math.Max(0, page), 1);
        }

        var first = page % 2 == 1 ? page : page - 1;
        return (first, Math.Min(2, pages - first));
    }

    private static string? ImageType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        _ => null,
    };

    // "page2.jpg" before "page10.jpg": runs of digits compare as numbers.
    private static string NaturalKey(string name) => Digits().Replace(name, match => match.Value.PadLeft(10, '0'));

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
