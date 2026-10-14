namespace Shelf.Api.Books;

// Which books on a shelf a row from another catalog means, whatever edition each is: the same ISBN, or the same
// author and title however written. An audiobook is often "The Way of Kings (Unabridged)", by "Brandon Sanderson,
// Michael Kramer", with an ISBN of its own, where Goodreads has "The Way of Kings" by Brandon Sanderson; a lecture
// course is "Heavens Above: Stars, Constellations, and the Sky" where Goodreads has "The Modern Scholar: Heavens
// Above: …", or names the series as its author.
public sealed class ShelfMatcher
{
    // A title this long, the same word for word, is the same book whoever it is said to be by.
    private const int AloneWords = 4;

    // The fewest telling words a title can share wholly with another of the same author's and be taken for it.
    private const int SharedWords = 3;

    private static readonly HashSet<string> Small = ["a", "an", "the", "of", "and", "or", "in", "on", "to", "for", "with", "from", "by", "at", "its", "his", "her", "their", "how", "why", "what"];

    private readonly Dictionary<string, List<int>> _keys = [];
    private readonly Dictionary<string, List<(int Id, HashSet<string> Words)>> _byAuthor = [];
    private readonly Dictionary<string, List<int>> _byTitle = [];

    public void Add(int id, string title, string author, string? isbn)
    {
        if (!string.IsNullOrWhiteSpace(isbn))
        {
            Note("isbn:" + isbn, id);
        }

        var authors = Authors(author);
        foreach (var name in Titles(title))
        {
            var (full, @base) = (Merging.FullTitle(name).Joined, Merging.BaseTitle(name).Joined);
            foreach (var key in authors)
            {
                Note($"full:{key}\n{full}", id);
                Note($"base:{key}\n{@base}", id);
            }

            // A file with no author named is matched by its whole title alone.
            if (author.Trim() == BookImport.UnknownAuthor)
            {
                Note($"full:\n{full}", id);
            }

            if (full.Split(' ').Length >= AloneWords)
            {
                Add(_byTitle, full, id);
            }
        }

        var words = Telling(title);
        foreach (var key in authors)
        {
            if (!_byAuthor.TryGetValue(key, out var list))
            {
                _byAuthor[key] = list = [];
            }

            list.Add((id, words));
        }
    }

    // The books a row means, or none. A title with its subtitle matches one without, but two different subtitles
    // ("Star Wars: Thrawn", "Star Wars: Heir to the Empire") don't match each other. Failing those, by the same
    // author, a title whose telling words are all in the other's; and failing that, a long title alone.
    public IReadOnlyCollection<int> Find(string title, string author, string? isbn, IEnumerable<string>? otherAuthors = null)
    {
        var found = new HashSet<int>();
        void Look(string key)
        {
            if (_keys.TryGetValue(key, out var ids))
            {
                found.UnionWith(ids);
            }
        }

        if (!string.IsNullOrWhiteSpace(isbn))
        {
            Look("isbn:" + isbn);
        }

        var authors = Authors(author);
        foreach (var other in otherAuthors ?? [])
        {
            authors.UnionWith(Authors(other));
        }

        var titles = Titles(title).ToList();
        foreach (var name in titles)
        {
            var (full, @base) = (Merging.FullTitle(name).Joined, Merging.BaseTitle(name).Joined);
            foreach (var key in authors)
            {
                Look($"full:{key}\n{full}");
                Look($"base:{key}\n{full}");
                Look($"full:{key}\n{@base}");
            }

            Look($"full:\n{full}");
        }

        if (found.Count > 0)
        {
            return found;
        }

        // The same author, and one title's telling words all within the other's: the closest, if one is closest.
        var words = Telling(title);
        var close = new List<(int Id, int Shared)>();
        foreach (var key in authors)
        {
            foreach (var (id, theirs) in _byAuthor.GetValueOrDefault(key) ?? [])
            {
                var shared = words.Count(theirs.Contains);
                var smaller = Math.Min(words.Count, theirs.Count);
                if (smaller >= SharedWords && shared == smaller)
                {
                    close.Add((id, shared));
                }
            }
        }

        if (close.Count > 0)
        {
            var best = close.Max(item => item.Shared);
            var leaders = close.Where(item => item.Shared == best).Select(item => item.Id).Distinct().ToList();
            if (leaders.Count == 1 || leaders.All(id => id < 0))
            {
                return leaders;
            }
        }

        // A long title, the same word for word, on one book only.
        foreach (var name in titles)
        {
            if (_byTitle.TryGetValue(Merging.FullTitle(name).Joined, out var ids) && ids.Distinct().Count() == 1)
            {
                return [ids[0]];
            }
        }

        return found;
    }

    private void Note(string key, int id)
    {
        if (key.EndsWith('\n'))
        {
            return;
        }

        Add(_keys, key, id);
    }

    private static void Add(Dictionary<string, List<int>> map, string key, int id)
    {
        if (!map.TryGetValue(key, out var ids))
        {
            map[key] = ids = [];
        }

        if (!ids.Contains(id))
        {
            ids.Add(id);
        }
    }

    // A title as it is; without a series written into it ("Stormlight Archive 01 - The Way of Kings"); and without
    // what comes before its first colon, as when a series leads ("The Modern Scholar: Heavens Above").
    private static IEnumerable<string> Titles(string title)
    {
        yield return title;
        if (Tidy.FindSeries(0, title) is { } series)
        {
            yield return series.NewTitle;
        }

        var colon = title.IndexOf(':');
        if (colon > 2 && colon < title.Length - 3)
        {
            yield return title[(colon + 1)..].Trim();
        }
    }

    // The words that tell one title from another: not "the", "of", or words that only say which edition it is.
    private static HashSet<string> Telling(string title) =>
        [.. Merging.FullTitle(title).Words.Where(word => !Small.Contains(word))];

    // The author however written, and each of several named together, as audiobooks often name their narrators.
    private static HashSet<string> Authors(string author)
    {
        var keys = new HashSet<string> { Tidy.AuthorKey(author) };
        foreach (var part in author.Split([";", "&", " and ", "/", "|"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            keys.Add(Tidy.AuthorKey(part));
            foreach (var name in part.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Tidy.Plain).Where(name => name.Contains(' ')))
            {
                keys.Add(name);
            }
        }

        keys.Remove("");
        return keys;
    }
}
