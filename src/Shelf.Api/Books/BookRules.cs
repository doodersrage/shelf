using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public static class BookRules
{
    public const int MaxTags = 8;
    public const int MaxTagLength = 40;

    public static Dictionary<string, string[]>? Validate(BookWrite write)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Add(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                list = [];
                errors[key] = list;
            }

            list.Add(message);
        }

        if (string.IsNullOrWhiteSpace(write.Title))
        {
            Add(nameof(write.Title), "Title is required.");
        }

        if (string.IsNullOrWhiteSpace(write.Author))
        {
            Add(nameof(write.Author), "Author is required.");
        }

        if (!string.IsNullOrWhiteSpace(write.Isbn) && NormalizeIsbn(write.Isbn) is null)
        {
            Add(nameof(write.Isbn), "Enter a 10- or 13-digit ISBN.");
        }

        if (write.FinishedOn is { } finished && write.StartedOn is { } started && finished < started)
        {
            Add(nameof(write.FinishedOn), "Finished date is before the start date.");
        }

        if (write.CurrentPage is { } page && write.Pages is { } pages && page > pages)
        {
            Add(nameof(write.CurrentPage), "Current page is past the end of the book.");
        }

        var tags = CanonicalTags(write.Tags);
        if (tags.Count > MaxTags)
        {
            Add(nameof(write.Tags), $"Use at most {MaxTags} tags.");
        }

        if (tags.Any(tag => tag.Length > MaxTagLength))
        {
            Add(nameof(write.Tags), $"Tags can be at most {MaxTagLength} characters.");
        }

        return errors.Count == 0
            ? null
            : errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    public static Dictionary<string, string[]>? ValidateQuote(string? text, int? page, int? bookPages)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(text))
        {
            errors[nameof(CreateQuoteRequest.Text)] = ["Quote text is required."];
        }

        if (page is { } quotePage && bookPages is { } pages && quotePage > pages)
        {
            errors[nameof(CreateQuoteRequest.Page)] = ["That page is past the end of the book."];
        }

        return errors.Count == 0
            ? null
            : errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    public static void Apply(Book book, BookWrite write)
    {
        var created = book.Id == 0;
        var previous = book.Status;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        book.Title = write.Title.Trim();
        book.Author = write.Author.Trim();
        book.Rating = write.Rating;
        book.Year = write.Year;
        book.Isbn = NormalizeIsbn(write.Isbn);
        book.Pages = write.Pages;
        book.CurrentPage = write.CurrentPage;
        book.Notes = BlankToNull(write.Notes);
        book.LoanedTo = BlankToNull(write.LoanedTo);
        book.StartedOn = write.StartedOn;
        book.FinishedOn = write.FinishedOn;
        book.Status = write.Status;

        if (book.AddedAt == default)
        {
            book.AddedAt = DateTimeOffset.UtcNow;
        }

        // Stamp the reading dates when a book enters Reading or Finished, unless a date was supplied.
        var statusChanged = created || previous != write.Status;
        if (statusChanged && write.Status is BookStatus.Reading or BookStatus.Finished)
        {
            book.StartedOn ??= today;
        }

        if (statusChanged && write.Status == BookStatus.Finished)
        {
            book.FinishedOn ??= today;
        }
    }

    public static void ChangeStatus(Book book, BookStatus status)
    {
        if (book.Status == status)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        book.Status = status;
        if (status is BookStatus.Reading or BookStatus.Finished)
        {
            book.StartedOn ??= today;
        }

        if (status == BookStatus.Finished)
        {
            book.FinishedOn ??= today;
        }
    }

    public static async Task SyncTagsAsync(ShelfDb db, Book book, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        var names = CanonicalTags(tags);
        var existing = names.Count == 0
            ? []
            : await db.Tags.Where(tag => names.Contains(tag.Name)).ToListAsync(cancellationToken);
        var byName = existing.ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);

        book.Tags.Clear();
        foreach (var name in names)
        {
            if (!byName.TryGetValue(name, out var tag))
            {
                tag = new Tag { Name = name };
                db.Tags.Add(tag);
                byName[name] = tag;
            }

            book.Tags.Add(tag);
        }
    }

    public static async Task RemoveUnusedTagsAsync(ShelfDb db, CancellationToken cancellationToken)
    {
        var unused = await db.Tags.Where(tag => !tag.Books.Any()).ToListAsync(cancellationToken);
        if (unused.Count == 0)
        {
            return;
        }

        db.Tags.RemoveRange(unused);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<ShelfStatsResponse> SummarizeAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var rows = await db.Books.AsNoTracking()
            .Select(book => new StatRow(book.Status, book.Rating, book.Pages, book.CurrentPage, book.FinishedOn))
            .ToListAsync(cancellationToken);

        var tags = await db.Tags.AsNoTracking()
            .Where(tag => tag.Books.Any())
            .Select(tag => new TagCountResponse(tag.Name, tag.Books.Count()))
            .ToListAsync(cancellationToken);

        var ratings = rows.Where(row => row.Rating is not null).Select(row => (double)row.Rating!.Value).ToList();
        var year = DateOnly.FromDateTime(DateTime.UtcNow).Year;

        return new ShelfStatsResponse(
            rows.Count,
            rows.Count(row => row.Status == BookStatus.Want),
            rows.Count(row => row.Status == BookStatus.Reading),
            rows.Count(row => row.Status == BookStatus.Finished),
            rows.Count(row => row.Status == BookStatus.Abandoned),
            ratings.Count == 0 ? null : Math.Round(ratings.Average(), 2),
            rows.Sum(PagesRead),
            rows.Count(row => row.Status == BookStatus.Finished && row.FinishedOn?.Year == year),
            tags.OrderByDescending(tag => tag.Count).ThenBy(tag => tag.Name, StringComparer.Ordinal).ToArray());
    }

    public static IQueryable<Book> Filtered(IQueryable<Book> books, string? q, BookStatus? status, string? tag)
    {
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var isbn = NormalizeIsbn(term);
            books = isbn is null
                ? books.Where(book =>
                    book.Title.Contains(term)
                    || book.Author.Contains(term)
                    || (book.Isbn != null && book.Isbn.Contains(term))
                    || (book.Notes != null && book.Notes.Contains(term))
                    || book.Quotes.Any(quote => quote.Text.Contains(term)))
                : books.Where(book =>
                    book.Title.Contains(term)
                    || book.Author.Contains(term)
                    || book.Isbn == isbn
                    || (book.Notes != null && book.Notes.Contains(term))
                    || book.Quotes.Any(quote => quote.Text.Contains(term)));
        }

        if (status is { } selected)
        {
            books = books.Where(book => book.Status == selected);
        }

        if (CanonicalTag(tag) is { } tagName)
        {
            books = books.Where(book => book.Tags.Any(item => item.Name == tagName));
        }

        return books;
    }

    public static List<Book> Sort(List<Book> books, string? sort)
    {
        // SQLite cannot order by DateTimeOffset, so every sort happens after the query.
        var ordered = sort?.Trim().ToLowerInvariant() switch
        {
            "author" => books.OrderBy(book => book.Author, StringComparer.OrdinalIgnoreCase),
            "year" => books.OrderByDescending(book => book.Year),
            "rating" => books.OrderByDescending(book => book.Rating),
            "added" => books.OrderByDescending(book => book.AddedAt),
            _ => books.OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase),
        };

        return ordered.ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase).ThenBy(book => book.Id).ToList();
    }

    public static IQueryable<Book> WithDetails(this IQueryable<Book> books) =>
        books.Include(book => book.Tags).Include(book => book.Quotes).AsSplitQuery();

    public static string? NormalizeIsbn(string? isbn)
    {
        if (string.IsNullOrWhiteSpace(isbn))
        {
            return null;
        }

        var filtered = new string(isbn.Where(character => character is not ('-' or ' ')).ToArray()).ToUpperInvariant();
        if (filtered.Length == 13 && filtered.All(char.IsDigit))
        {
            return filtered;
        }

        if (filtered.Length == 10
            && filtered[..9].All(char.IsDigit)
            && (char.IsDigit(filtered[9]) || filtered[9] == 'X'))
        {
            return filtered;
        }

        return null;
    }

    public static List<string> CanonicalTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return [];
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            if (CanonicalTag(tag) is not { } name || !seen.Add(name))
            {
                continue;
            }

            names.Add(name);
        }

        return names;
    }

    public static string? CanonicalTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.ToLowerInvariant();
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int PagesRead(StatRow row) => row.Status switch
    {
        BookStatus.Finished => row.Pages ?? 0,
        BookStatus.Reading => row.CurrentPage ?? 0,
        _ => 0,
    };

    private sealed record StatRow(BookStatus Status, int? Rating, int? Pages, int? CurrentPage, DateOnly? FinishedOn);
}
