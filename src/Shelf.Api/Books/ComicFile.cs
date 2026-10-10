using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Shelf.Api.Books;

// A comic book archive (.cbz): page images in a zip, read in name order, with an optional ComicInfo.xml.
public static partial class ComicFile
{
    private const long MaxPageBytes = 40L * 1024 * 1024;

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
