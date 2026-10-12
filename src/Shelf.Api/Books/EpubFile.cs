using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Shelf.Api.Books;

public sealed record EpubChapter(int Index, string Title, string Href);

public static partial class EpubFile
{
    // A book's chapters are asked for with every page of it, so kept for the file as it is (a changed file is read
    // afresh), for the books read lately.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Length, DateTime Written), IReadOnlyList<EpubChapter>?> Known = new();

    public static IReadOnlyList<EpubChapter>? Chapters(string path)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }

        var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
        if (Known.TryGetValue(key, out var known))
        {
            return known;
        }

        var chapters = Read(path);
        if (Known.Count > 200)
        {
            Known.Clear();
        }

        Known[key] = chapters;
        return chapters;
    }

    private static IReadOnlyList<EpubChapter>? Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var opfPath = PackagePath(zip);
            if (opfPath is null)
            {
                return null;
            }

            var package = Load(zip, opfPath);
            if (package is null)
            {
                return null;
            }

            var manifest = package.Descendants()
                .Where(element => element.Name.LocalName == "item")
                .Select(element => (Id: element.Attribute("id")?.Value, Href: element.Attribute("href")?.Value))
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
                .ToDictionary(item => item.Id!, item => item.Href!, StringComparer.Ordinal);
            var contents = Contents(zip, opfPath, package, manifest);

            var chapters = new List<EpubChapter>();
            foreach (var itemRef in package.Descendants().Where(element => element.Name.LocalName == "itemref"))
            {
                var id = itemRef.Attribute("idref")?.Value;
                if (id is null || !manifest.TryGetValue(id, out var href))
                {
                    continue;
                }

                var fullHref = Combine(opfPath, href);
                // The book's own table of contents names a chapter best; else its heading, else its file.
                var title = (contents.TryGetValue(fullHref, out var listed) ? listed : null)
                    ?? ChapterTitle(zip, fullHref)
                    ?? Path.GetFileNameWithoutExtension(fullHref);
                chapters.Add(new EpubChapter(chapters.Count, title.Trim(), fullHref));
            }

            return chapters;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException)
        {
            return null;
        }
    }

    public static string? ChapterHtml(string path, int index, string assetRoot)
    {
        var chapters = Chapters(path);
        if (chapters is null || index < 0 || index >= chapters.Count)
        {
            return null;
        }

        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = Find(zip, chapters[index].Href);
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var html = StripActiveContent(reader.ReadToEnd());
            var directory = DirectoryOf(chapters[index].Href);
            var baseHref = assetRoot.TrimEnd('/') + "/" + EscapePath(directory);
            if (!baseHref.EndsWith('/'))
            {
                baseHref += "/";
            }

            var baseTag = $"""<base href="{baseHref}" />""";
            var head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
            if (head >= 0)
            {
                var close = html.IndexOf('>', head);
                if (close >= 0)
                {
                    return html.Insert(close + 1, baseTag);
                }
            }

            return baseTag + html;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException)
        {
            return null;
        }
    }

    // A chapter as XML, for finding a place in it: the doctype ignored, HTML's named characters read as themselves,
    // and what the reader strips (scripts, frames, forms) taken out, so its elements are counted as the reader's are.
    public static XDocument? ChapterDocument(string path, int index)
    {
        var chapters = Chapters(path);
        if (chapters is null || index < 0 || index >= chapters.Count)
        {
            return null;
        }

        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (Find(zip, chapters[index].Href) is not { } entry)
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return ChapterDocument(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    public static XDocument? ChapterDocument(string xhtml)
    {
        var text = NamedCharacter().Replace(xhtml, match => match.Groups[1].Value is "amp" or "lt" or "gt" or "quot" or "apos"
            ? match.Value
            : System.Net.WebUtility.HtmlDecode(match.Value) is { Length: 1 or 2 } decoded && decoded != match.Value
                ? string.Concat(decoded.EnumerateRunes().Select(rune => $"&#{rune.Value};"))
                : "");
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var document = XDocument.Load(reader);
            document.Descendants().Where(element => Stripped.Contains(element.Name.LocalName.ToLowerInvariant())).ToList().ForEach(element => element.Remove());
            return document;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static readonly HashSet<string> Stripped = ["script", "iframe", "frame", "frameset", "object", "embed", "applet", "form", "base"];

    [GeneratedRegex("&([A-Za-z][A-Za-z0-9]*);")]
    private static partial Regex NamedCharacter();

    public static (byte[] Bytes, string ContentType)? Asset(string path, string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath) || assetPath.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = Find(zip, assetPath);
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return (memory.ToArray(), ContentType(assetPath));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static string? PackagePath(ZipArchive zip)
    {
        var container = Find(zip, "META-INF/container.xml");
        if (container is null)
        {
            return null;
        }

        var document = Load(container);
        return document?.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile")
            ?.Attribute("full-path")?.Value;
    }

    // A chapter's own heading, else its <title> (which many books set to the book's title in every file).
    private static string? ChapterTitle(ZipArchive zip, string href)
    {
        var document = LoadTolerant(zip, href);
        if (document is null)
        {
            return null;
        }

        var heading = document.Descendants().FirstOrDefault(element => element.Name.LocalName is "h1" or "h2" or "h3");
        var title = string.IsNullOrWhiteSpace(heading?.Value) ? document.Descendants().FirstOrDefault(element => element.Name.LocalName == "title") : heading;
        return Clean(title?.Value);
    }

    // The book's table of contents, by file: EPUB 3's navigation document (its toc), else EPUB 2's NCX. The first
    // entry for a file names it; an entry pointing into the middle of a file (#fragment) counts for the file.
    private static Dictionary<string, string> Contents(ZipArchive zip, string opfPath, XDocument package, Dictionary<string, string> manifest)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var items = package.Descendants().Where(element => element.Name.LocalName == "item").ToList();
            var nav = items.FirstOrDefault(item => (item.Attribute("properties")?.Value ?? "").Split(' ').Contains("nav"))?.Attribute("href")?.Value;
            if (nav is not null && LoadTolerant(zip, Combine(opfPath, nav)) is { } navigation)
            {
                var navPath = Combine(opfPath, nav);
                var toc = navigation.Descendants().Where(element => element.Name.LocalName == "nav")
                    .FirstOrDefault(element => element.Attributes().Any(attribute => attribute.Name.LocalName == "type" && attribute.Value.Split(' ').Contains("toc")))
                    ?? navigation.Descendants().FirstOrDefault(element => element.Name.LocalName == "nav");
                foreach (var link in toc?.Descendants().Where(element => element.Name.LocalName == "a") ?? [])
                {
                    Add(navPath, link.Attribute("href")?.Value, link.Value);
                }
            }

            if (found.Count == 0)
            {
                var ncxId = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine")?.Attribute("toc")?.Value;
                var ncx = ncxId is not null && manifest.TryGetValue(ncxId, out var ncxHref) ? ncxHref
                    : items.FirstOrDefault(item => item.Attribute("media-type")?.Value == "application/x-dtbncx+xml")?.Attribute("href")?.Value;
                if (ncx is not null && LoadTolerant(zip, Combine(opfPath, ncx)) is { } document)
                {
                    var ncxPath = Combine(opfPath, ncx);
                    foreach (var point in document.Descendants().Where(element => element.Name.LocalName == "navPoint"))
                    {
                        var label = point.Elements().FirstOrDefault(element => element.Name.LocalName == "navLabel")?.Value;
                        var src = point.Elements().FirstOrDefault(element => element.Name.LocalName == "content")?.Attribute("src")?.Value;
                        Add(ncxPath, src, label);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or UriFormatException)
        {
            // A table of contents that can't be read leaves the chapters to their own headings.
        }

        return found;

        void Add(string from, string? href, string? label)
        {
            if (string.IsNullOrWhiteSpace(href) || Clean(label) is not { } name || href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            found.TryAdd(Combine(from, href), name);
        }
    }

    private static XDocument? LoadTolerant(ZipArchive zip, string path)
    {
        if (Find(zip, path) is not { } entry)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ChapterDocument(reader.ReadToEnd());
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var entry = Find(zip, path);
        return entry is null ? null : Load(entry);
    }

    private static XDocument? Load(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    internal static ZipArchiveEntry? Find(ZipArchive zip, string path)
    {
        var wanted = path.Replace('\\', '/').TrimStart('/');
        if (wanted.Length == 0 || wanted.Split('/').Contains(".."))
        {
            return null;
        }

        return zip.Entries.FirstOrDefault(entry =>
            entry.FullName.Replace('\\', '/').TrimStart('/').Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    internal static string Combine(string packagePath, string href)
    {
        var relative = Uri.UnescapeDataString(href.Split('#')[0]).Replace('\\', '/');
        if (relative.StartsWith('/'))
        {
            return relative.TrimStart('/');
        }

        var combined = string.IsNullOrEmpty(DirectoryOf(packagePath))
            ? relative
            : DirectoryOf(packagePath) + "/" + relative;
        var parts = new List<string>();
        foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    private static string DirectoryOf(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? "" : normalized[..slash];
    }

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static string StripActiveContent(string html) =>
        JsUrls().Replace(OnAttributes().Replace(Scripts().Replace(html, ""), ""), "");

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".css" => "text/css",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".xhtml" or ".html" or ".htm" => "text/html; charset=utf-8",
        _ => "application/octet-stream",
    };

    [GeneratedRegex("""<script\b[^>]*>.*?</script>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Scripts();

    [GeneratedRegex("""\s+on[a-z]+\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OnAttributes();

    [GeneratedRegex("""javascript\s*:""", RegexOptions.IgnoreCase)]
    private static partial Regex JsUrls();
}
