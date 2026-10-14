namespace Shelf.Api.Books;

// Which books on a shelf a row from another catalog means, whatever edition each is: the same ISBN, or the same
// author and title however written. An audiobook is often "The Way of Kings (Unabridged)", by "Brandon Sanderson,
// Michael Kramer", with an ISBN of its own, where Goodreads has "The Way of Kings" by Brandon Sanderson.
public sealed class ShelfMatcher
{
    private readonly Dictionary<string, List<int>> _keys = [];

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
        }
    }

    // The books a row means, or none. A title with its subtitle matches one without, but two different subtitles
    // ("Star Wars: Thrawn", "Star Wars: Heir to the Empire") don't match each other.
    public IReadOnlyCollection<int> Find(string title, string author, string? isbn)
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
        foreach (var name in Titles(title))
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

        return found;
    }

    private void Note(string key, int id)
    {
        if (key.EndsWith('\n'))
        {
            return;
        }

        if (!_keys.TryGetValue(key, out var ids))
        {
            _keys[key] = ids = [];
        }

        if (!ids.Contains(id))
        {
            ids.Add(id);
        }
    }

    // A title as it is, and without a series written into it ("Stormlight Archive 01 - The Way of Kings").
    private static IEnumerable<string> Titles(string title)
    {
        yield return title;
        if (Tidy.FindSeries(0, title) is { } series)
        {
            yield return series.NewTitle;
        }
    }

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
