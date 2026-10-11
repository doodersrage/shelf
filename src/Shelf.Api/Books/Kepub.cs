using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Shelf.Api.Books;

// Kobo's own kind of EPUB (KEPUB): the same book, with each sentence in a marked span and the body in two wrappers.
// A Kobo reads a KEPUB with page numbers, time left in the chapter, and its reading statistics; a plain EPUB it reads
// with its older engine, and none of those. This does what kepubify does, enough for a Kobo to treat it as its own.
public static partial class KepubConverter
{
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "li", "dd", "dt", "td", "th", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "figcaption", "caption", "section", "article", "aside", "header", "footer",
    };

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "svg", "math", "head", "title" };

    // False when the EPUB could not be read; a chapter that will not parse is kept as it was.
    public static bool Convert(string epub, string kepub)
    {
        try
        {
            using var source = ZipFile.OpenRead(epub);
            var chapters = ChapterPaths(source);
            var partial = kepub + ".part";
            using (var file = File.Create(partial))
            using (var target = new ZipArchive(file, ZipArchiveMode.Create))
            {
                // The mimetype comes first and unpacked, as the EPUB format asks.
                if (source.GetEntry("mimetype") is { } mimetype)
                {
                    Copy(mimetype, target.CreateEntry("mimetype", CompressionLevel.NoCompression));
                }

                foreach (var entry in source.Entries.Where(entry => entry.FullName != "mimetype" && !entry.FullName.EndsWith('/')))
                {
                    var made = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    if (chapters.Contains(entry.FullName))
                    {
                        string html;
                        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                        {
                            html = reader.ReadToEnd();
                        }

                        using var writer = new StreamWriter(made.Open(), new UTF8Encoding(false));
                        writer.Write(Chapter(html));
                    }
                    else
                    {
                        Copy(entry, made);
                    }
                }
            }

            File.Move(partial, kepub, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Copy(ZipArchiveEntry from, ZipArchiveEntry to)
    {
        using var input = from.Open();
        using var output = to.Open();
        input.CopyTo(output);
    }

    // The XHTML files the package lists.
    private static HashSet<string> ChapterPaths(ZipArchive zip)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var container = zip.GetEntry("META-INF/container.xml");
        if (container is null)
        {
            return found;
        }

        XDocument Load(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            return XDocument.Load(XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }));
        }

        var opfPath = Load(container).Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        if (opfPath is null || zip.GetEntry(opfPath) is not { } opf)
        {
            return found;
        }

        var folder = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
        foreach (var item in Load(opf).Descendants().Where(element => element.Name.LocalName == "item" && element.Attribute("media-type")?.Value == "application/xhtml+xml"))
        {
            if (item.Attribute("href")?.Value is { } href)
            {
                found.Add(Normalize(folder + Uri.UnescapeDataString(href)));
            }
        }

        return found;
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part is not ("." or ""))
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }

    // One chapter: its sentences in spans numbered by paragraph and sentence, its body in Kobo's wrappers.
    public static string Chapter(string html)
    {
        XDocument document;
        try
        {
            // Named entities other than XML's own come from HTML, and need no DTD once they are numbers.
            var numbered = Entity().Replace(html, match => match.Groups[1].Value is "amp" or "lt" or "gt" or "quot" or "apos"
                ? match.Value
                : WebUtility.HtmlDecode(match.Value) is { Length: > 0 } decoded && decoded != match.Value ? string.Concat(decoded.Select(c => $"&#{(int)c};")) : match.Value);
            using var reader = XmlReader.Create(new StringReader(numbered), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return html;
        }

        var body = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
        if (body is null || body.Descendants().Any(element => element.Attribute("class")?.Value?.Contains("koboSpan", StringComparison.Ordinal) == true))
        {
            return html;
        }

        var ns = body.Name.Namespace;
        var paragraph = 0;
        var sentence = 0;
        XElement? block = null;
        foreach (var text in body.DescendantNodes().OfType<XText>().Where(text => text is not XCData).ToList())
        {
            if (string.IsNullOrWhiteSpace(text.Value) || text.Ancestors().Any(element => Skipped.Contains(element.Name.LocalName)))
            {
                continue;
            }

            var around = text.Ancestors().FirstOrDefault(element => Blocks.Contains(element.Name.LocalName)) ?? text.Parent;
            if (around != block)
            {
                (block, paragraph, sentence) = (around, paragraph + 1, 0);
            }

            var pieces = new List<XNode>();
            foreach (var part in Sentence().Split(text.Value))
            {
                if (part.Length == 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(part))
                {
                    pieces.Add(new XText(part));
                    continue;
                }

                // Space before or after a sentence stays outside its span.
                var lead = part.Length - part.TrimStart().Length;
                var trail = part.Length - part.TrimEnd().Length;
                if (lead > 0)
                {
                    pieces.Add(new XText(part[..lead]));
                }

                pieces.Add(new XElement(ns + "span", new XAttribute("class", "koboSpan"), new XAttribute("id", $"kobo.{paragraph}.{++sentence}"), part.Trim()));
                if (trail > 0)
                {
                    pieces.Add(new XText(part[^trail..]));
                }
            }

            text.ReplaceWith(pieces);
        }

        var inner = new XElement(ns + "div", new XAttribute("id", "book-inner"), body.Nodes().ToList());
        body.RemoveNodes();
        body.Add(new XElement(ns + "div", new XAttribute("id", "book-columns"), inner));
        var head = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "head");
        head?.Add(new XElement(ns + "style", new XAttribute("type", "text/css"), new XAttribute("class", "kobostylehacks"), "div#book-inner { margin-top: 0; margin-bottom: 0; }"));

        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = document.Declaration is null, Encoding = new UTF8Encoding(false) }))
        {
            document.Save(writer);
        }

        // XmlWriter calls the encoding of a string UTF-16; the file is UTF-8.
        return output.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("&([A-Za-z][A-Za-z0-9]*);")]
    private static partial Regex Entity();

    // A sentence ends at . ! ? or … (with any closing quote or bracket), before the space after it.
    [GeneratedRegex(@"(?<=[.!?…]['""”’)\]]*)(?=\s)")]
    private static partial Regex Sentence();
}

// KEPUBs made from the shelf's EPUBs, kept beside them so each is made once (Kobo:Kepub turns it off).
public sealed class KepubStore(EbookStore ebooks, IConfiguration configuration, ILogger<KepubStore> logger)
{
    private readonly SemaphoreSlim making = new(1, 1);

    public bool Enabled => configuration.GetValue("Kobo:Kepub", true);

    private string Root => Path.Combine(ebooks.Root, "kepub");

    // The KEPUB for a stored EPUB, made now if it is not there yet; null when it cannot be made.
    public async Task<string?> PathForAsync(string? storedName, CancellationToken cancellationToken = default)
    {
        if (!Enabled || ebooks.OpenPath(storedName) is not { } epub || !EbookStore.IsEpub(storedName))
        {
            return null;
        }

        var path = Path.Combine(Root, Path.GetFileNameWithoutExtension(storedName) + ".kepub.epub");
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= File.GetLastWriteTimeUtc(epub))
        {
            return path;
        }

        await making.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Root);
            if (await Task.Run(() => KepubConverter.Convert(epub, path), cancellationToken))
            {
                return path;
            }

            logger.LogInformation("Could not make a KEPUB of {Book}; the Kobo gets the EPUB.", storedName);
            return null;
        }
        finally
        {
            making.Release();
        }
    }

    // The KEPUB's size when it is made already, else the EPUB's, for the Kobo's progress bar.
    public long SizeOf(string? storedName)
    {
        var path = storedName is null ? null : Path.Combine(Root, Path.GetFileNameWithoutExtension(storedName) + ".kepub.epub");
        return path is not null && File.Exists(path) ? new FileInfo(path).Length : ebooks.OpenPath(storedName) is { } epub ? new FileInfo(epub).Length : 0;
    }

    // KEPUBs whose EPUB is gone.
    public void Sweep(IReadOnlySet<string> storedNames)
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(Root, "*.kepub.epub"))
        {
            var stem = Path.GetFileName(file)[..^".kepub.epub".Length];
            if (!storedNames.Any(name => Path.GetFileNameWithoutExtension(name) == stem))
            {
                File.Delete(file);
            }
        }
    }
}
