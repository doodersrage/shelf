using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Shelf.Api.Books;

public sealed record EpubChapter(int Index, string Title, string Href);

public static partial class EpubFile
{
    public static IReadOnlyList<EpubChapter>? Chapters(string path)
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

            var chapters = new List<EpubChapter>();
            foreach (var itemRef in package.Descendants().Where(element => element.Name.LocalName == "itemref"))
            {
                var id = itemRef.Attribute("idref")?.Value;
                if (id is null || !manifest.TryGetValue(id, out var href))
                {
                    continue;
                }

                var fullHref = Combine(opfPath, href);
                var title = ChapterTitle(zip, fullHref) ?? Path.GetFileNameWithoutExtension(fullHref);
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

    private static string? ChapterTitle(ZipArchive zip, string href)
    {
        var entry = Find(zip, href);
        var document = entry is null ? null : Load(entry);
        if (document is null)
        {
            return null;
        }

        var title = document.Descendants().FirstOrDefault(element => element.Name.LocalName is "title" or "h1");
        return string.IsNullOrWhiteSpace(title?.Value) ? null : title.Value;
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
