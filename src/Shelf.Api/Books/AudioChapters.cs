using System.Buffers.Binary;
using System.Text;

namespace Shelf.Api.Books;

public sealed record AudioChapter(int Index, string Title, double Start);

// Reads the chapter marks in an .m4b or .m4a audiobook: the Nero "chpl" list when there is one, otherwise the
// QuickTime chapter track. Only the small "moov" index is read, so a long book is never loaded whole.
public static class AudioChapters
{
    private const long MaxIndexBytes = 64L * 1024 * 1024;

    public static IReadOnlyList<AudioChapter> Read(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not ".m4b" and not ".m4a" and not ".mp4")
        {
            return [];
        }

        try
        {
            using var file = File.OpenRead(path);
            var moov = FindTopLevel(file, "moov");
            if (moov is null)
            {
                return [];
            }

            var chapters = Nero(moov) ?? QuickTime(moov, file) ?? [];
            return chapters.Count > 1 ? chapters : [];
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            // A damaged or unusual file simply has no chapters.
            return [];
        }
    }

    private static byte[]? FindTopLevel(FileStream file, string wanted)
    {
        Span<byte> header = stackalloc byte[16];
        long position = 0;
        while (position + 8 <= file.Length)
        {
            file.Position = position;
            file.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header[4..8]);
            var headerLength = 8;
            if (size == 1)
            {
                file.ReadExactly(header[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = file.Length - position;
            }

            if (size < headerLength)
            {
                return null;
            }

            if (type == wanted)
            {
                if (size > MaxIndexBytes)
                {
                    return null;
                }

                var body = new byte[size - headerLength];
                file.ReadExactly(body);
                return body;
            }

            position += size;
        }

        return null;
    }

    private static IEnumerable<(string Type, int Start, int End)> Children(byte[] data, int start, int end)
    {
        var offset = start;
        while (offset + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            var type = Encoding.ASCII.GetString(data, offset + 4, 4);
            var header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - offset;
            }

            if (size < header || offset + size > end)
            {
                yield break;
            }

            yield return (type, offset + header, (int)(offset + size));
            offset += (int)size;
        }
    }

    private static (int Start, int End)? Child(byte[] data, (int Start, int End) parent, string type)
    {
        foreach (var child in Children(data, parent.Start, parent.End))
        {
            if (child.Type == type)
            {
                return (child.Start, child.End);
            }
        }

        return null;
    }

    // moov/udta/chpl: a version and flags, four reserved bytes in version 1, a count, then each chapter's start
    // in 100-nanosecond units and its title.
    private static List<AudioChapter>? Nero(byte[] moov)
    {
        var udta = Child(moov, (0, moov.Length), "udta");
        var chpl = udta is null ? null : Child(moov, udta.Value, "chpl");
        if (chpl is not { } box)
        {
            return null;
        }

        var offset = box.Start;
        var version = moov[offset];
        offset += 4 + (version == 1 ? 4 : 0);
        int count = moov[offset++];
        var chapters = new List<AudioChapter>();
        for (var index = 0; index < count && offset + 9 <= box.End; index++)
        {
            var start = BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(offset)) / 10_000_000.0;
            int length = moov[offset + 8];
            offset += 9;
            if (offset + length > box.End)
            {
                break;
            }

            chapters.Add(new AudioChapter(index, Title(Encoding.UTF8.GetString(moov, offset, length), index), start));
            offset += length;
        }

        return chapters.Count > 0 ? chapters : null;
    }

    // The QuickTime way: the audio track's tref/chap names a text track whose samples are the chapter titles,
    // each starting where the durations before it add up to.
    private static List<AudioChapter>? QuickTime(byte[] moov, FileStream file)
    {
        var tracks = Children(moov, 0, moov.Length).Where(child => child.Type == "trak").Select(child => (child.Start, child.End)).ToList();
        uint? chapterTrack = null;
        foreach (var trak in tracks)
        {
            if (Child(moov, trak, "tref") is { } tref && Child(moov, tref, "chap") is { } chap && chap.End - chap.Start >= 4)
            {
                chapterTrack = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(chap.Start));
                break;
            }
        }

        if (chapterTrack is null)
        {
            return null;
        }

        foreach (var trak in tracks)
        {
            if (Child(moov, trak, "tkhd") is not { } tkhd)
            {
                continue;
            }

            var idOffset = tkhd.Start + (moov[tkhd.Start] == 1 ? 4 + 16 : 4 + 8);
            if (BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(idOffset)) != chapterTrack)
            {
                continue;
            }

            var mdia = Child(moov, trak, "mdia");
            var mdhd = mdia is null ? null : Child(moov, mdia.Value, "mdhd");
            var minf = mdia is null ? null : Child(moov, mdia.Value, "minf");
            var stbl = minf is null ? null : Child(moov, minf.Value, "stbl");
            if (mdhd is null || stbl is null)
            {
                return null;
            }

            var timescale = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mdhd.Value.Start + (moov[mdhd.Value.Start] == 1 ? 4 + 16 : 4 + 8)));
            var durations = SampleDurations(moov, Child(moov, stbl.Value, "stts"));
            var sizes = SampleSizes(moov, Child(moov, stbl.Value, "stsz"));
            var offsets = SampleOffsets(moov, Child(moov, stbl.Value, "stsc"), Child(moov, stbl.Value, "stco"), Child(moov, stbl.Value, "co64"), sizes);
            if (timescale == 0 || sizes.Count == 0 || offsets.Count != sizes.Count)
            {
                return null;
            }

            var chapters = new List<AudioChapter>();
            double time = 0;
            Span<byte> lengthBytes = stackalloc byte[2];
            for (var index = 0; index < sizes.Count; index++)
            {
                var title = "";
                if (sizes[index] >= 2 && sizes[index] < 4096)
                {
                    file.Position = offsets[index];
                    file.ReadExactly(lengthBytes);
                    var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(lengthBytes), sizes[index] - 2);
                    var text = new byte[length];
                    file.ReadExactly(text);
                    title = Encoding.UTF8.GetString(text);
                }

                chapters.Add(new AudioChapter(index, Title(title, index), time));
                time += (index < durations.Count ? durations[index] : 0) / (double)timescale;
            }

            return chapters;
        }

        return null;
    }

    private static List<long> SampleDurations(byte[] data, (int Start, int End)? stts)
    {
        var durations = new List<long>();
        if (stts is not { } box)
        {
            return durations;
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.Start + 4));
        for (var entry = 0; entry < count && box.Start + 8 + entry * 8 + 8 <= box.End; entry++)
        {
            var samples = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.Start + 8 + entry * 8));
            var delta = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.Start + 12 + entry * 8));
            for (var sample = 0; sample < samples && durations.Count < 10_000; sample++)
            {
                durations.Add(delta);
            }
        }

        return durations;
    }

    private static List<int> SampleSizes(byte[] data, (int Start, int End)? stsz)
    {
        var sizes = new List<int>();
        if (stsz is not { } box)
        {
            return sizes;
        }

        var fixedSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.Start + 4));
        var count = Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.Start + 8)), 10_000u);
        for (var index = 0; index < count; index++)
        {
            sizes.Add(fixedSize != 0 ? (int)fixedSize : (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.Start + 12 + index * 4)));
        }

        return sizes;
    }

    private static List<long> SampleOffsets(byte[] data, (int Start, int End)? stsc, (int Start, int End)? stco, (int Start, int End)? co64, List<int> sizes)
    {
        var chunks = new List<long>();
        if (stco is { } small)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(small.Start + 4));
            for (var index = 0; index < count; index++)
            {
                chunks.Add(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(small.Start + 8 + index * 4)));
            }
        }
        else if (co64 is { } large)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(large.Start + 4));
            for (var index = 0; index < count; index++)
            {
                chunks.Add((long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(large.Start + 8 + index * 8)));
            }
        }

        var runs = new List<(uint FirstChunk, uint PerChunk)>();
        if (stsc is { } map)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(map.Start + 4));
            for (var index = 0; index < count; index++)
            {
                runs.Add((BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(map.Start + 8 + index * 12)),
                    BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(map.Start + 12 + index * 12))));
            }
        }

        var offsets = new List<long>();
        var sample = 0;
        for (var chunk = 0; chunk < chunks.Count && sample < sizes.Count; chunk++)
        {
            var perChunk = runs.LastOrDefault(run => run.FirstChunk <= chunk + 1).PerChunk;
            var position = chunks[chunk];
            for (var inChunk = 0; inChunk < Math.Max(1, perChunk) && sample < sizes.Count; inChunk++)
            {
                offsets.Add(position);
                position += sizes[sample++];
            }
        }

        return offsets;
    }

    private static string Title(string title, int index) =>
        string.IsNullOrWhiteSpace(title) ? $"Chapter {index + 1}" : title.Trim();
}
