using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Shelf.Api.Books;

// What a file says about the book inside it. Any of it may be missing; a file name is the last resort for a title.
public sealed record FileDetails(
    string? Title,
    string? Author,
    string? Isbn = null,
    string? Publisher = null,
    int? Year = null,
    string? Language = null,
    string? Album = null);

public static partial class EpubFile
{
    // The package's Dublin Core: title, creator, identifier (an ISBN when it is one), publisher, date, language.
    public static FileDetails? Details(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var opfPath = PackagePath(zip);
            var package = opfPath is null ? null : Load(zip, opfPath);
            var metadata = package?.Descendants().FirstOrDefault(element => element.Name.LocalName == "metadata");
            if (metadata is null)
            {
                return null;
            }

            string? First(string name) => metadata.Elements()
                .Where(element => element.Name.LocalName == name)
                .Select(element => Clean(element.Value))
                .FirstOrDefault(value => value is not null);

            var authors = metadata.Elements()
                .Where(element => element.Name.LocalName == "creator")
                .Where(element => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "role")?.Value is null or "aut")
                .Select(element => Clean(element.Value))
                .OfType<string>()
                .Take(3)
                .ToList();
            var isbn = metadata.Elements()
                .Where(element => element.Name.LocalName == "identifier")
                .Select(element => BookRules.NormalizeIsbn(Regex.Replace(element.Value, "^(urn:)?isbn:", "", RegexOptions.IgnoreCase)))
                .FirstOrDefault(value => value is not null);
            var date = First("date");
            int? year = date is { Length: >= 4 } && int.TryParse(date[..4], out var parsed) && parsed is > 0 and < 3000 ? parsed : null;
            return new FileDetails(First("title"), authors.Count == 0 ? null : string.Join(", ", authors), isbn, First("publisher"), year, First("language"));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException)
        {
            return null;
        }
    }

    // The cover picture: the manifest item marked cover-image (EPUB 3), or the one a <meta name="cover"> names (EPUB 2).
    public static (byte[] Bytes, string ContentType)? Cover(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var opfPath = PackagePath(zip);
            var package = opfPath is null ? null : Load(zip, opfPath);
            if (package is null)
            {
                return null;
            }

            var items = package.Descendants().Where(element => element.Name.LocalName == "item").ToList();
            var coverId = package.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "meta" && element.Attribute("name")?.Value == "cover")
                ?.Attribute("content")?.Value;
            var item = items.FirstOrDefault(element => (element.Attribute("properties")?.Value ?? "").Split(' ').Contains("cover-image"))
                ?? items.FirstOrDefault(element => coverId is not null && element.Attribute("id")?.Value == coverId);
            var href = item?.Attribute("href")?.Value;
            var type = item?.Attribute("media-type")?.Value;
            if (href is null || type is null || !type.StartsWith("image/", StringComparison.Ordinal) || type == "image/svg+xml")
            {
                return null;
            }

            var entry = Find(zip, Combine(opfPath!, href));
            if (entry is null || entry.Length is 0 or > 10 * 1024 * 1024)
            {
                return null;
            }

            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return (copy.ToArray(), type);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException)
        {
            return null;
        }
    }

    private static string? Clean(string? value)
    {
        var trimmed = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return trimmed.Length == 0 ? null : trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }
}

public static class PdfDetails
{
    // pdfinfo's Title and Author, when Poppler is installed and the PDF has them.
    public static async Task<FileDetails?> ReadAsync(string path, string pdfinfo, CancellationToken cancellationToken)
    {
        var output = await Tools.RunAsync(pdfinfo, [path], TimeSpan.FromSeconds(20), cancellationToken);
        if (output is null)
        {
            return null;
        }

        string? Field(string name) => output.Split('\n')
            .Where(line => line.StartsWith(name + ":", StringComparison.Ordinal))
            .Select(line => line[(name.Length + 1)..].Trim())
            .FirstOrDefault(value => value.Length > 0 && value.Length <= 200);
        var date = Field("CreationDate");
        var year = date is null ? null : YearIn(date);
        return new FileDetails(Field("Title"), Field("Author"), Year: year);
    }

    private static int? YearIn(string text)
    {
        var match = Regex.Match(text, @"\b(1[5-9]\d\d|20\d\d)\b");
        return match.Success ? int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}

// Tags in a recording: iTunes-style atoms in an .m4b or .m4a, ID3v2 frames in an MP3.
public static class AudioDetails
{
    public static FileDetails? Read(string path)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".m4b" or ".m4a" or ".mp4" or ".aac" => Mp4(path),
                ".mp3" => Id3(path),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or IndexOutOfRangeException or OverflowException or DecoderFallbackException)
        {
            return null;
        }
    }

    // moov/udta/meta/ilst: each tag is a box named ©nam, ©ART, aART, ©alb, ©day holding a "data" box.
    private static FileDetails? Mp4(string path)
    {
        using var file = File.OpenRead(path);
        var moov = AudioChapters.MoovOf(file);
        if (moov is null)
        {
            return null;
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(moov, 0, moov.Length, ["udta", "meta", "ilst"], 0, tags);
        if (tags.Count == 0)
        {
            return null;
        }

        tags.TryGetValue("©nam", out var title);
        tags.TryGetValue("©alb", out var album);
        var author = tags.GetValueOrDefault("aART") ?? tags.GetValueOrDefault("©ART") ?? tags.GetValueOrDefault("©wrt");
        int? year = tags.TryGetValue("©day", out var day) && day.Length >= 4 && int.TryParse(day[..4], out var parsed) ? parsed : null;
        // An audiobook's album is the book; the title is often a chapter.
        return new FileDetails(album ?? title, author, Year: year, Album: album);
    }

    private static void Walk(byte[] data, int start, int end, string[] path, int depth, Dictionary<string, string> tags)
    {
        var offset = start;
        while (offset + 8 <= end)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            var type = Encoding.Latin1.GetString(data, offset + 4, 4);
            if (size < 8 || offset + size > end)
            {
                return;
            }

            if (depth < path.Length && type == path[depth])
            {
                // "meta" is a full box: four bytes of version and flags before its children.
                var childStart = offset + 8 + (type == "meta" ? 4 : 0);
                Walk(data, childStart, offset + size, path, depth + 1, tags);
            }
            else if (depth == path.Length && offset + 24 <= offset + size)
            {
                var dataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 8));
                if (Encoding.Latin1.GetString(data, offset + 12, 4) == "data" && dataSize >= 16 && offset + 8 + dataSize <= offset + size)
                {
                    var text = Encoding.UTF8.GetString(data, offset + 24, dataSize - 16).Trim('\0', ' ');
                    if (text.Length is > 0 and <= 200)
                    {
                        tags.TryAdd(type, text);
                    }
                }
            }

            offset += size;
        }
    }

    // ID3v2.3 and 2.4: a ten-byte header, then frames named TIT2 (title), TPE1 (artist), TALB (album), TYER/TDRC (year).
    private static FileDetails? Id3(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[10];
        if (file.Read(header) != 10 || header[0] != 'I' || header[1] != 'D' || header[2] != '3' || header[3] is not (3 or 4))
        {
            return null;
        }

        var version = header[3];
        var length = Synchsafe(header[6..10]);
        if (length is <= 0 or > 4 * 1024 * 1024)
        {
            return null;
        }

        var tag = new byte[length];
        file.ReadExactly(tag);
        var frames = new Dictionary<string, string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset + 10 <= tag.Length && tag[offset] != 0)
        {
            var id = Encoding.Latin1.GetString(tag, offset, 4);
            var size = version == 4 ? Synchsafe(tag.AsSpan(offset + 4, 4)) : (int)BinaryPrimitives.ReadUInt32BigEndian(tag.AsSpan(offset + 4));
            if (size <= 0 || offset + 10 + size > tag.Length)
            {
                break;
            }

            if (id[0] == 'T' && size > 1)
            {
                var text = DecodeText(tag[offset + 10], tag.AsSpan(offset + 11, size - 1));
                if (text.Length is > 0 and <= 200)
                {
                    frames.TryAdd(id, text);
                }
            }

            offset += 10 + size;
        }

        if (frames.Count == 0)
        {
            return null;
        }

        frames.TryGetValue("TALB", out var album);
        frames.TryGetValue("TIT2", out var title);
        var author = frames.GetValueOrDefault("TPE2") ?? frames.GetValueOrDefault("TPE1") ?? frames.GetValueOrDefault("TCOM");
        var date = frames.GetValueOrDefault("TDRC") ?? frames.GetValueOrDefault("TYER");
        int? year = date is { Length: >= 4 } && int.TryParse(date[..4], out var parsed) ? parsed : null;
        return new FileDetails(album ?? title, author, Year: year, Album: album);
    }

    private static int Synchsafe(ReadOnlySpan<byte> bytes) =>
        (bytes[0] & 0x7f) << 21 | (bytes[1] & 0x7f) << 14 | (bytes[2] & 0x7f) << 7 | (bytes[3] & 0x7f);

    // 0 Latin-1, 1 UTF-16 with a byte-order mark, 2 UTF-16 big-endian, 3 UTF-8.
    private static string DecodeText(byte encoding, ReadOnlySpan<byte> bytes)
    {
        string text;
        if (encoding == 1)
        {
            var bigEndian = bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF;
            var marked = bigEndian || (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE);
            var body = marked ? bytes[2..] : bytes;
            text = bigEndian ? Encoding.BigEndianUnicode.GetString(body) : Encoding.Unicode.GetString(body);
        }
        else
        {
            text = encoding switch
            {
                2 => Encoding.BigEndianUnicode.GetString(bytes),
                3 => Encoding.UTF8.GetString(bytes),
                _ => Encoding.Latin1.GetString(bytes),
            };
        }

        return text.Split('\0')[0].Trim();
    }
}

public static class Tools
{
    // Runs an installed program and returns what it printed, or null when it is missing, fails, or takes too long.
    public static async Task<string?> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            var text = await output;
            await errors;
            return process.ExitCode == 0 ? text : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
