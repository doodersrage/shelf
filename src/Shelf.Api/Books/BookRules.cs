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

        if (write.SeriesNumber is not null && string.IsNullOrWhiteSpace(write.Series))
        {
            Add(nameof(write.Series), "A series number needs a series name.");
        }

        if (!string.IsNullOrWhiteSpace(write.CoverUrl) && !IsCoverUrl(write.CoverUrl))
        {
            Add(nameof(write.CoverUrl), "Cover address must start with http:// or https://.");
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

    public static Dictionary<string, string[]>? ValidateSession(CreateSessionRequest request, int? bookPages)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (request.FromPage is { } from && request.ToPage is { } to && to < from)
        {
            errors[nameof(request.ToPage)] = ["The session ends before it starts."];
        }

        if (request.ToPage is { } end && bookPages is { } pages && end > pages)
        {
            errors[nameof(request.ToPage)] = ["That page is past the end of the book."];
        }

        if (!string.IsNullOrWhiteSpace(request.Note) && request.Note.Trim().Length > 500)
        {
            errors[nameof(request.Note)] = ["Keep the session note to 500 characters."];
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
        book.Subtitle = BlankToNull(write.Subtitle);
        book.Publisher = BlankToNull(write.Publisher);
        book.Language = BlankToNull(write.Language);
        book.Format = write.Format;
        book.Series = BlankToNull(write.Series);
        book.SeriesNumber = book.Series is null ? null : write.SeriesNumber;
        book.CoverUrl = BlankToNull(write.CoverUrl);
        book.Review = BlankToNull(write.Review);
        book.Loved = write.Loved;
        book.StartedOn = write.StartedOn;
        book.FinishedOn = write.FinishedOn;
        book.Status = write.Status;

        var previousLoan = book.LoanedTo;
        book.LoanedTo = BlankToNull(write.LoanedTo);
        if (book.LoanedTo is null)
        {
            book.LoanedOn = null;
        }
        else
        {
            book.LoanedOn = write.LoanedOn;
            if (book.LoanedOn is null && !string.Equals(previousLoan, book.LoanedTo, StringComparison.Ordinal))
            {
                book.LoanedOn = today;
            }
        }

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
            .Select(book => new StatRow(
                book.Status,
                book.Rating,
                book.Pages,
                book.CurrentPage,
                book.FinishedOn,
                book.Loved,
                book.LoanedTo != null))
            .ToListAsync(cancellationToken);
        var goal = await GetGoalAsync(db, cancellationToken);

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
            tags.OrderByDescending(tag => tag.Count).ThenBy(tag => tag.Name, StringComparer.Ordinal).ToArray(),
            rows.Count(row => row.Loved),
            rows.Count(row => row.OnLoan),
            goal);
    }

    public static async Task<int> GetGoalAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(item => item.Id == ShelfSettingId, cancellationToken);
        return setting?.YearlyGoal ?? 0;
    }

    public static async Task SetGoalAsync(ShelfDb db, int goal, CancellationToken cancellationToken = default)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(item => item.Id == ShelfSettingId, cancellationToken);
        if (setting is null)
        {
            db.Settings.Add(new ShelfSetting { Id = ShelfSettingId, YearlyGoal = goal });
        }
        else
        {
            setting.YearlyGoal = goal;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<ImportResult> ImportAsync(ShelfDb db, LibraryExport export, CancellationToken cancellationToken = default)
    {
        var existing = await db.Books.AsNoTracking()
            .Select(book => new ExistingBook(book.Title, book.Author, book.Isbn))
            .ToListAsync(cancellationToken);
        var seen = existing.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;

        foreach (var source in export.Books)
        {
            var write = BookWrite.From(source);
            if (Validate(write) is not null || !seen.Add(Key(new ExistingBook(write.Title, write.Author, NormalizeIsbn(write.Isbn)))))
            {
                skipped++;
                continue;
            }

            var book = new Book
            {
                Title = write.Title.Trim(),
                Author = write.Author.Trim(),
            };
            Apply(book, write);
            if (source.AddedAt != default)
            {
                book.AddedAt = source.AddedAt;
            }

            foreach (var quote in source.Quotes)
            {
                if (string.IsNullOrWhiteSpace(quote.Text))
                {
                    continue;
                }

                book.Quotes.Add(new Quote
                {
                    Text = quote.Text.Trim(),
                    Page = quote.Page,
                    NotedAt = quote.NotedAt == default ? DateTimeOffset.UtcNow : quote.NotedAt,
                });
            }

            foreach (var session in source.Sessions)
            {
                book.Sessions.Add(new ReadingSession
                {
                    Date = session.Date,
                    FromPage = session.FromPage,
                    ToPage = session.ToPage,
                    Note = string.IsNullOrWhiteSpace(session.Note) ? null : session.Note.Trim(),
                });
            }

            db.Books.Add(book);
            await SyncTagsAsync(db, book, write.Tags, cancellationToken);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await RemoveUnusedTagsAsync(db, cancellationToken);
        }

        if (export.YearlyGoal > 0 && await GetGoalAsync(db, cancellationToken) == 0)
        {
            await SetGoalAsync(db, export.YearlyGoal, cancellationToken);
        }

        return new ImportResult(added, skipped);
    }

    public static void AdvanceProgress(Book book, int? toPage)
    {
        if (toPage is not { } page)
        {
            return;
        }

        if (book.CurrentPage is null || page > book.CurrentPage)
        {
            book.CurrentPage = page;
        }
    }

    public static void ReturnLoan(Book book)
    {
        book.LoanedTo = null;
        book.LoanedOn = null;
    }

    public static int? PagesPerDay(IEnumerable<ReadingSession> sessions)
    {
        var logged = sessions.Where(session => session.ToPage is int).ToList();
        if (logged.Count == 0)
        {
            return null;
        }

        var pages = logged.Max(session => session.ToPage ?? 0) - logged.Min(session => session.FromPage ?? 0);
        if (pages <= 0)
        {
            return null;
        }

        var days = Math.Max(1, logged.Max(session => session.Date).DayNumber - logged.Min(session => session.Date).DayNumber);
        return Math.Max(1, pages / days);
    }

    public static bool IsSameCopy(
        string? isbn,
        string title,
        string author,
        string? otherIsbn,
        string otherTitle,
        string otherAuthor)
    {
        var left = NormalizeIsbn(isbn);
        var right = NormalizeIsbn(otherIsbn);
        if (left is not null && left == right)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author))
        {
            return false;
        }

        return title.Trim().Equals(otherTitle.Trim(), StringComparison.OrdinalIgnoreCase)
            && author.Trim().Equals(otherAuthor.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static IQueryable<Book> Filtered(
        IQueryable<Book> books,
        string? q,
        BookStatus? status,
        string? tag,
        string? author = null,
        bool? loved = null,
        bool? loaned = null,
        BookFormat? format = null)
    {
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var isbn = NormalizeIsbn(term);
            books = isbn is null
                ? books.Where(book =>
                    book.Title.Contains(term)
                    || book.Author.Contains(term)
                    || (book.Subtitle != null && book.Subtitle.Contains(term))
                    || (book.Series != null && book.Series.Contains(term))
                    || (book.Publisher != null && book.Publisher.Contains(term))
                    || (book.Isbn != null && book.Isbn.Contains(term))
                    || (book.Notes != null && book.Notes.Contains(term))
                    || (book.Review != null && book.Review.Contains(term))
                    || book.Quotes.Any(quote => quote.Text.Contains(term)))
                : books.Where(book =>
                    book.Title.Contains(term)
                    || book.Author.Contains(term)
                    || (book.Subtitle != null && book.Subtitle.Contains(term))
                    || (book.Series != null && book.Series.Contains(term))
                    || book.Isbn == isbn
                    || (book.Notes != null && book.Notes.Contains(term))
                    || (book.Review != null && book.Review.Contains(term))
                    || book.Quotes.Any(quote => quote.Text.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            var name = author.Trim().ToLower();
            books = books.Where(book => book.Author.ToLower() == name);
        }

        if (loved is { } lovedOnly)
        {
            books = books.Where(book => book.Loved == lovedOnly);
        }

        if (loaned == true)
        {
            books = books.Where(book => book.LoanedTo != null);
        }
        else if (loaned == false)
        {
            books = books.Where(book => book.LoanedTo == null);
        }

        if (format is { } selectedFormat)
        {
            books = books.Where(book => book.Format == selectedFormat);
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
            "series" => books
                .OrderBy(book => book.Series == null)
                .ThenBy(book => book.Series, StringComparer.OrdinalIgnoreCase)
                .ThenBy(book => book.SeriesNumber ?? int.MaxValue),
            _ => books.OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase),
        };

        return ordered.ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase).ThenBy(book => book.Id).ToList();
    }

    public static IQueryable<Book> WithDetails(this IQueryable<Book> books) =>
        books.Include(book => book.Tags).Include(book => book.Quotes).Include(book => book.Sessions).AsSplitQuery();

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

    private const int ShelfSettingId = 1;

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsCoverUrl(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 500
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https";
    }

    private static string Key(ExistingBook book)
    {
        if (!string.IsNullOrWhiteSpace(book.Isbn))
        {
            return "isbn:" + book.Isbn;
        }

        return "title:" + book.Title.Trim().ToLowerInvariant() + "\n" + book.Author.Trim().ToLowerInvariant();
    }

    private static int PagesRead(StatRow row) => row.Status switch
    {
        BookStatus.Finished => row.Pages ?? 0,
        BookStatus.Reading => row.CurrentPage ?? 0,
        _ => 0,
    };

    private sealed record StatRow(
        BookStatus Status,
        int? Rating,
        int? Pages,
        int? CurrentPage,
        DateOnly? FinishedOn,
        bool Loved,
        bool OnLoan);

    private sealed record ExistingBook(string Title, string Author, string? Isbn);
}
