using System.Buffers.Binary;
using System.Text;

namespace Shelf.Api.Books;

// How long a recording runs, read from the few bytes that say so rather than by decoding it: an MP4's movie header,
// an MP3's Xing or VBRI header (or its bit rate, for a constant one), the last Ogg page's sample count, FLAC's stream
// info, or a WAV's data size. Null when the file does not say (a raw AAC stream, say); the player then asks the
// browser instead.
public static class AudioLength
{
    public static double? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var seconds = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".m4a" or ".m4b" or ".mp4" => Mp4(file),
                ".mp3" => Mp3(file),
                ".ogg" or ".opus" => Ogg(file),
                ".flac" => Flac(file),
                ".wav" => Wav(file),
                _ => null,
            };
            return seconds is > 0 and < 1_000_000 ? Math.Round(seconds.Value, 3) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException or EndOfStreamException or OverflowException)
        {
            return null;
        }
    }

    // moov/mvhd: a version, flags, two times, the time scale, and the duration in that scale.
    private static double? Mp4(FileStream file)
    {
        var moov = AudioChapters.MoovOf(file);
        if (moov is null)
        {
            return null;
        }

        var offset = 0;
        while (offset + 8 <= moov.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(offset));
            if (size < 8 || offset + size > moov.Length)
            {
                return null;
            }

            if (Encoding.ASCII.GetString(moov, offset + 4, 4) == "mvhd")
            {
                var body = moov.AsSpan(offset + 8);
                return body[0] == 1
                    ? Seconds(BinaryPrimitives.ReadUInt64BigEndian(body[24..]), BinaryPrimitives.ReadUInt32BigEndian(body[20..]))
                    : Seconds(BinaryPrimitives.ReadUInt32BigEndian(body[16..]), BinaryPrimitives.ReadUInt32BigEndian(body[12..]));
            }

            offset += size;
        }

        return null;
    }

    private static readonly int[] Mpeg1Layer3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg1Layer2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] Mpeg2Layer23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] SampleRates = [44100, 48000, 32000];

    private static double? Mp3(FileStream file)
    {
        // An ID3v2 tag at the front: ten bytes of header, then its size in seven-bit bytes.
        Span<byte> header = stackalloc byte[10];
        file.ReadExactly(header);
        long start = 0;
        if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
        {
            start = 10 + ((header[6] & 0x7F) << 21 | (header[7] & 0x7F) << 14 | (header[8] & 0x7F) << 7 | (header[9] & 0x7F)) + ((header[5] & 0x10) != 0 ? 10 : 0);
        }

        // The first frame: an eleven-bit sync, then its version, layer, bit rate, and sample rate.
        var window = new byte[16 * 1024];
        file.Position = start;
        var read = file.Read(window);
        for (var at = 0; at + 4 <= read; at++)
        {
            if (window[at] != 0xFF || (window[at + 1] & 0xE0) != 0xE0)
            {
                continue;
            }

            var version = (window[at + 1] >> 3) & 3; // 3 MPEG-1, 2 MPEG-2, 0 MPEG-2.5
            var layer = (window[at + 1] >> 1) & 3;   // 1 layer III, 2 layer II, 3 layer I
            var rateIndex = (window[at + 2] >> 4) & 15;
            var sampleIndex = (window[at + 2] >> 2) & 3;
            if (version == 1 || layer == 0 || rateIndex is 0 or 15 || sampleIndex == 3)
            {
                continue;
            }

            var mpeg1 = version == 3;
            var sampleRate = SampleRates[sampleIndex] / (mpeg1 ? 1 : version == 2 ? 2 : 4);
            var samplesPerFrame = layer == 3 ? 384 : layer == 2 || mpeg1 ? 1152 : 576;
            var bitRate = (mpeg1 ? layer == 1 ? Mpeg1Layer3 : Mpeg1Layer2 : Mpeg2Layer23)[rateIndex] * 1000;
            var mono = ((window[at + 3] >> 6) & 3) == 3;

            // A variable bit rate says how many frames it has: Xing (or Info) after the side information, or VBRI.
            var xing = at + 4 + (mpeg1 ? mono ? 17 : 32 : mono ? 9 : 17);
            if (xing + 12 <= read && Tag(window, xing) is "Xing" or "Info" && (window[xing + 7] & 1) != 0)
            {
                return Seconds(BinaryPrimitives.ReadUInt32BigEndian(window.AsSpan(xing + 8)) * (ulong)samplesPerFrame, (uint)sampleRate);
            }

            if (at + 36 + 18 <= read && Tag(window, at + 36) == "VBRI")
            {
                return Seconds(BinaryPrimitives.ReadUInt32BigEndian(window.AsSpan(at + 36 + 14)) * (ulong)samplesPerFrame, (uint)sampleRate);
            }

            // A constant bit rate: the audio's bytes at that rate, less an ID3v1 tag at the end.
            var end = file.Length;
            if (end >= 128)
            {
                Span<byte> tail = stackalloc byte[3];
                file.Position = end - 128;
                file.ReadExactly(tail);
                end -= tail[0] == 'T' && tail[1] == 'A' && tail[2] == 'G' ? 128 : 0;
            }

            return bitRate > 0 ? (end - start - at) * 8.0 / bitRate : null;
        }

        return null;
    }

    // The last page's granule position is the sample count: at the stream's own rate for Vorbis, always 48 kHz for
    // Opus, less its pre-skip.
    private static double? Ogg(FileStream file)
    {
        var head = new byte[512];
        var headLength = file.Read(head);
        var text = Encoding.ASCII.GetString(head, 0, headLength);
        double rate;
        long skip = 0;
        if (text.IndexOf("OpusHead", StringComparison.Ordinal) is var opus and >= 0 && opus + 12 <= headLength)
        {
            rate = 48000;
            skip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(opus + 10));
        }
        else if (text.IndexOf("vorbis", StringComparison.Ordinal) is var vorbis and >= 0 && vorbis + 15 <= headLength)
        {
            rate = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(vorbis + 11));
        }
        else
        {
            return null;
        }

        var size = (int)Math.Min(file.Length, 64 * 1024);
        var tail = new byte[size];
        file.Position = file.Length - size;
        file.ReadExactly(tail);
        for (var at = size - 14; at >= 0; at--)
        {
            if (tail[at] == 'O' && tail[at + 1] == 'g' && tail[at + 2] == 'g' && tail[at + 3] == 'S')
            {
                var granule = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(at + 6));
                return rate > 0 && granule > skip ? (granule - skip) / rate : null;
            }
        }

        return null;
    }

    // fLaC, then the STREAMINFO block: twenty bits of sample rate and thirty-six of total samples.
    private static double? Flac(FileStream file)
    {
        Span<byte> head = stackalloc byte[26];
        file.ReadExactly(head);
        if (head[0] != 'f' || head[1] != 'L' || head[2] != 'a' || head[3] != 'C' || (head[4] & 0x7F) != 0)
        {
            return null;
        }

        var packed = BinaryPrimitives.ReadUInt64BigEndian(head[18..]);
        var rate = (uint)(packed >> 44);
        return Seconds(packed & 0xF_FFFF_FFFF, rate);
    }

    // RIFF chunks: the format's bytes a second, and the data's size.
    private static double? Wav(FileStream file)
    {
        Span<byte> riff = stackalloc byte[12];
        file.ReadExactly(riff);
        if (Encoding.ASCII.GetString(riff[..4]) != "RIFF" || Encoding.ASCII.GetString(riff[8..12]) != "WAVE")
        {
            return null;
        }

        uint bytesPerSecond = 0;
        Span<byte> chunk = stackalloc byte[8];
        while (file.Position + 8 <= file.Length)
        {
            file.ReadExactly(chunk);
            var id = Encoding.ASCII.GetString(chunk[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (id == "fmt " && size >= 12)
            {
                Span<byte> format = stackalloc byte[12];
                file.ReadExactly(format);
                bytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
                file.Position += size - 12 + (size & 1);
            }
            else if (id == "data")
            {
                return bytesPerSecond > 0 ? Math.Min(size, file.Length - file.Position) / (double)bytesPerSecond : null;
            }
            else
            {
                file.Position += size + (size & 1);
            }
        }

        return null;
    }

    private static string Tag(byte[] data, int at) => Encoding.ASCII.GetString(data, at, 4);

    private static double? Seconds(ulong units, uint scale) => scale == 0 ? null : units / (double)scale;
}
