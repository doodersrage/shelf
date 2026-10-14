using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// The columns the library's compact table can show beside each book's title and author.
public static class LibraryColumns
{
    public static readonly string[] All = ["status", "series", "year", "format", "place", "rating", "pages", "added", "finished", "loan"];

    public static readonly string[] Default = ["status", "format", "place", "added"];

    public static string Label(string column) => column switch
    {
        "status" => T("Status"),
        "series" => T("Series"),
        "year" => T("Year"),
        "format" => T("Format"),
        "place" => T("Place"),
        "rating" => T("Rating"),
        "pages" => T("Pages"),
        "added" => T("Added"),
        "finished" => T("Finished"),
        "loan" => T("On loan"),
        _ => column,
    };

    // Kept as they were chosen, in the table's own order; none chosen, or nothing known, is the default.
    public static string[] Parse(string? stored)
    {
        var chosen = (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var known = All.Where(chosen.Contains).ToArray();
        return stored is null || (known.Length == 0 && stored != "") ? Default : known;
    }

    public static string Store(IEnumerable<string> columns) => string.Join(',', All.Where(columns.Contains));
}
