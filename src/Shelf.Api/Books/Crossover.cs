using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Shelf.Api.Books;

// One part of a book in one format: an EPUB chapter, an audiobook chapter, or a track. Its weight is how long it is,
// in letters for an e-book and in seconds (or bytes, which follow seconds closely) for audio.
public sealed record BookPart(string Title, double Weight);

// A place in one format: which part, and how far through it, from 0 to 1.
public sealed record PartSpot(int Part, double Within);

// Carries a place from an audiobook to its e-book and back. Parts whose titles agree (Chapter 7 and Seven, say) are
// pinned to each other; between those pins, and where nothing agrees, the place moves by how far through the
// text or the recording it is. The front matter of an e-book (its cover, title page, and contents) is left out of
// that reckoning, as a recording rarely reads it.
public sealed partial class Crossover
{
    private readonly double[] audioStarts;
    private readonly double[] ebookStarts;
    private readonly List<(double Audio, double Ebook)> pins = [];

    public Crossover(IReadOnlyList<BookPart> audio, IReadOnlyList<BookPart> ebook)
    {
        if (audio.Count == 0 || ebook.Count == 0)
        {
            throw new ArgumentException("Both formats need at least one part.");
        }

        audioStarts = Starts(audio);
        ebookStarts = Starts(ebook);

        // Titles that agree, in order: each audio part looks forward from the last one pinned.
        var keys = ebook.Select(part => Key(part.Title)).ToArray();
        var next = 0;
        var matched = new List<(int Audio, int Ebook)>();
        for (var a = 0; a < audio.Count; a++)
        {
            var key = Key(audio[a].Title);
            if (key.Length == 0)
            {
                continue;
            }

            for (var e = next; e < ebook.Count; e++)
            {
                if (Agree(key, keys[e]))
                {
                    matched.Add((a, e));
                    next = e + 1;
                    break;
                }
            }
        }

        // With no titles to go by, parts are paired in order when both formats have as many.
        var content = Enumerable.Range(0, ebook.Count).Where(index => IsContent(ebook, index)).ToList();
        if (matched.Count == 0 && audio.Count > 1 && audio.Count == content.Count)
        {
            matched = content.Select((e, a) => (a, e)).ToList();
        }

        Matched = matched.Count;
        var first = content.Count > 0 ? content[0] : 0;
        var last = content.Count > 0 ? content[^1] : ebook.Count - 1;
        // The recording starts where the story does, unless its first part is pinned already; it ends where the
        // story ends, or after the last part pinned, whichever is later.
        if (matched.Count == 0 || (matched[0].Audio > 0 && ebookStarts[first] < ebookStarts[matched[0].Ebook]))
        {
            Pin(0, ebookStarts[first]);
        }

        foreach (var (a, e) in matched)
        {
            Pin(audioStarts[a], ebookStarts[e]);
        }

        Pin(1, Math.Max(ebookStarts[last + 1], matched.Count > 0 ? ebookStarts[matched[^1].Ebook + 1] : 0));
    }

    // How many parts were pinned by their titles (or paired in order).
    public int Matched { get; }

    public PartSpot ToEbook(PartSpot audio) => Spot(ebookStarts, Map(At(audioStarts, audio), pins.Select(pin => (pin.Audio, pin.Ebook)).ToList()));

    public PartSpot ToAudio(PartSpot ebook) => Spot(audioStarts, Map(At(ebookStarts, ebook), pins.Select(pin => (pin.Ebook, pin.Audio)).ToList()));

    // The parts of an EPUB, by chapter, weighed by their letters; null when it is not an EPUB that opens.
    public static IReadOnlyList<BookPart>? EbookParts(EbookStore store, string? storedName, IMemoryCache cache)
    {
        if (!EbookStore.IsEpub(storedName) || store.OpenPath(storedName) is not { } path)
        {
            return null;
        }

        return cache.GetOrCreate($"crossover:{storedName}", entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromHours(1);
            var chapters = EpubFile.Chapters(path);
            return chapters is null or { Count: 0 }
                ? null
                : chapters.Select(chapter => new BookPart(chapter.Title, Math.Max(1, EpubFile.ChapterHtml(path, chapter.Index, "") is { } html ? Search.PlainText(html).Length : 1))).ToList();
        });
    }

    // The parts of an audiobook: the chapters marked in a single file, or else its tracks, weighed by their size.
    public static IReadOnlyList<BookPart> AudioParts(AudioStore store, string? storedName)
    {
        var chapters = store.Chapters(storedName);
        if (chapters.Count > 1)
        {
            var lengths = chapters.Zip(chapters.Skip(1), (one, next) => Math.Max(1, next.Start - one.Start)).ToList();
            var typical = lengths.Order().ElementAt(lengths.Count / 2);
            return chapters.Select((chapter, index) => new BookPart(chapter.Title, index < lengths.Count ? lengths[index] : typical)).ToList();
        }

        return store.Tracks(storedName)
            .Select(track => new BookPart(track.Title, Math.Max(1, store.TrackPath(storedName, track.Index) is { } file ? new FileInfo(file).Length : 1)))
            .ToList();
    }

    private void Pin(double audio, double ebook)
    {
        // Pins only ever move forward in both formats.
        if (pins.Count > 0 && (audio <= pins[^1].Audio || ebook <= pins[^1].Ebook))
        {
            return;
        }

        pins.Add((audio, ebook));
    }

    private static double Map(double from, List<(double From, double To)> line)
    {
        if (from <= line[0].From)
        {
            return line[0].To;
        }

        for (var i = 1; i < line.Count; i++)
        {
            if (from <= line[i].From)
            {
                var (x0, y0) = line[i - 1];
                var (x1, y1) = line[i];
                return y0 + ((from - x0) / (x1 - x0) * (y1 - y0));
            }
        }

        return line[^1].To;
    }

    // Where each part starts, from 0 to 1, with 1 at the end.
    private static double[] Starts(IReadOnlyList<BookPart> parts)
    {
        var total = parts.Sum(part => Math.Max(part.Weight, 0.0001));
        var starts = new double[parts.Count + 1];
        for (var i = 0; i < parts.Count; i++)
        {
            starts[i + 1] = starts[i] + (Math.Max(parts[i].Weight, 0.0001) / total);
        }

        starts[^1] = 1;
        return starts;
    }

    private static double At(double[] starts, PartSpot spot)
    {
        var part = Math.Clamp(spot.Part, 0, starts.Length - 2);
        return starts[part] + (Math.Clamp(spot.Within, 0, 1) * (starts[part + 1] - starts[part]));
    }

    private static PartSpot Spot(double[] starts, double at)
    {
        for (var part = 0; part < starts.Length - 1; part++)
        {
            if (at < starts[part + 1] || part == starts.Length - 2)
            {
                var length = starts[part + 1] - starts[part];
                return new PartSpot(part, length <= 0 ? 0 : Math.Round(Math.Clamp((at - starts[part]) / length, 0, 1), 4));
            }
        }

        return new PartSpot(0, 0);
    }

    // A chapter of the story rather than a cover or a contents page: at least a fifth of a typical chapter's length.
    private static bool IsContent(IReadOnlyList<BookPart> parts, int index)
    {
        var typical = parts.Select(part => part.Weight).Order().ElementAt(parts.Count / 2);
        return parts[index].Weight >= typical / 5;
    }

    // Two titles agree when they say the same, or when one ends with the other ("The Hobbit - Chapter 1" and
    // "Chapter One"); a bare number has to agree in full.
    private static bool Agree(string[] one, string[] other)
    {
        if (one.Length == 0 || other.Length == 0)
        {
            return false;
        }

        var (shorter, longer) = one.Length <= other.Length ? (one, other) : (other, one);
        if (shorter.Length == 1 && shorter[0].All(char.IsAsciiDigit) && longer.Length > 1)
        {
            return false;
        }

        return longer.AsSpan(longer.Length - shorter.Length).SequenceEqual(shorter);
    }

    // A title as words, with numbers written out or in Roman numerals as digits, and a leading track number gone.
    public static string[] Key(string? title)
    {
        var plain = new StringBuilder();
        foreach (var letter in (title ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(letter) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(char.ToLowerInvariant(letter));
            }
        }

        var words = Words().Matches(plain.ToString()).Select(match => match.Value).ToList();
        var key = new List<string>();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (Tens.TryGetValue(word, out var tens) && i + 1 < words.Count && Ones.TryGetValue(words[i + 1], out var unit) && unit < 10)
            {
                key.Add((tens + unit).ToString(CultureInfo.InvariantCulture));
                i++;
            }
            else if (Ones.TryGetValue(word, out var small) || Tens.TryGetValue(word, out small))
            {
                key.Add(small.ToString(CultureInfo.InvariantCulture));
            }
            else if (key.Count > 0 && key[^1] is "chapter" or "part" or "book" && Roman(word) is int roman)
            {
                key.Add(roman.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                key.Add(word.All(char.IsAsciiDigit) ? word.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0" : word);
            }
        }

        // "03 Chapter 2": the track's own number goes when a title follows it.
        if (key.Count > 1 && key[0].All(char.IsAsciiDigit) && !key[1].All(char.IsAsciiDigit))
        {
            key.RemoveAt(0);
        }

        return key.ToArray();
    }

    private static int? Roman(string word)
    {
        var values = new Dictionary<char, int> { ['i'] = 1, ['v'] = 5, ['x'] = 10, ['l'] = 50, ['c'] = 100 };
        if (word.Length == 0 || word.Any(letter => !values.ContainsKey(letter)))
        {
            return null;
        }

        var total = 0;
        for (var i = 0; i < word.Length; i++)
        {
            var value = values[word[i]];
            total += i + 1 < word.Length && values[word[i + 1]] > value ? -value : value;
        }

        return total;
    }

    private static readonly Dictionary<string, int> Ones = new(StringComparer.Ordinal)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.Ordinal)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
