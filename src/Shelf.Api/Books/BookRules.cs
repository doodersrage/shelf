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
        book.Location = BlankToNull(write.Location);
        book.AcquiredOn = write.AcquiredOn;
        book.Translator = BlankToNull(write.Translator);
        book.OriginalTitle = BlankToNull(write.OriginalTitle);
        book.Inscription = BlankToNull(write.Inscription);
        book.RecommendedBy = BlankToNull(write.RecommendedBy);
        book.StartedOn = write.StartedOn;
        book.FinishedOn = write.FinishedOn;
        book.Status = write.Status;

        var previousLoan = book.LoanedTo;
        book.LoanedTo = BlankToNull(write.LoanedTo);
        if (book.LoanedTo is null)
        {
            book.LoanedOn = null;
            book.DueOn = null;
        }
        else
        {
            book.LoanedOn = write.LoanedOn;
            if (book.LoanedOn is null && !string.Equals(previousLoan, book.LoanedTo, StringComparison.Ordinal))
            {
                book.LoanedOn = today;
            }

            book.DueOn = write.DueOn;
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
        book.DueOn = null;
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

    public static int? PagesRemaining(Book book)
    {
        if (book.Status != BookStatus.Reading || book.Pages is not int pages)
        {
            return null;
        }

        return Math.Max(0, pages - (book.CurrentPage ?? 0));
    }

    public static int? DaysRemaining(Book book)
    {
        if (PagesRemaining(book) is not int left)
        {
            return null;
        }

        if (left == 0)
        {
            return 0;
        }

        if (PagesPerDay(book.Sessions) is not int pace)
        {
            return null;
        }

        return Math.Max(1, (left + pace - 1) / pace);
    }

    public static Book? Pick(IReadOnlyList<Book> books, BookStatus status, DateOnly day)
    {
        var candidates = books.Where(book => book.Status == status).OrderBy(book => book.Id).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates[(int)((uint)day.DayNumber % (uint)candidates.Count)];
    }

    public static FinishedYear[] FinishedByYear(IEnumerable<Book> books) =>
        books
            .Where(book => book.Status == BookStatus.Finished)
            .GroupBy(book => book.FinishedOn?.Year)
            .Select(group => new FinishedYear(
                group.Key,
                group
                    .OrderByDescending(book => book.FinishedOn ?? DateOnly.MinValue)
                    .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(book => new YearBook(book.Id, book.Title, book.FinishedOn))
                    .ToArray()))
            .OrderByDescending(group => group.Year.HasValue)
            .ThenByDescending(group => group.Year)
            .ToArray();

    public static FinishedBook[] FinishedInYear(IEnumerable<Book> books, int year) =>
        books
            .Where(book => book.Status == BookStatus.Finished && book.FinishedOn?.Year == year)
            .OrderByDescending(book => book.FinishedOn)
            .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .Select(book => new FinishedBook(book.Id, book.Title, book.FinishedOn!.Value))
            .ToArray();

    public static RecentSession[] RecentSessions(IEnumerable<Book> books, int take = 8) =>
        books
            .SelectMany(book => book.Sessions.Select(session =>
                new RecentSession(book.Id, book.Title, session.Date, session.FromPage, session.ToPage)))
            .OrderByDescending(session => session.Date)
            .ThenByDescending(session => session.BookId)
            .Take(take)
            .ToArray();

    public static Book? NextInSeries(Book current, IEnumerable<Book> shelf)
    {
        if (string.IsNullOrWhiteSpace(current.Series) || current.SeriesNumber is not int number)
        {
            return null;
        }

        return shelf
            .Where(book => book.Id != current.Id
                && book.Series != null
                && book.Series.Equals(current.Series, StringComparison.OrdinalIgnoreCase)
                && book.SeriesNumber is int next && next > number)
            .OrderBy(book => book.SeriesNumber)
            .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static SeriesShelf[] SeriesShelves(IEnumerable<Book> books) =>
        books
            .Where(book => !string.IsNullOrWhiteSpace(book.Series))
            .GroupBy(book => book.Series!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new SeriesShelf(
                group.First().Series!.Trim(),
                group
                    .OrderBy(book => book.SeriesNumber ?? int.MaxValue)
                    .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(book => new SeriesBook(book.Id, book.Title, book.SeriesNumber, book.Status))
                    .ToArray()))
            .OrderBy(shelf => shelf.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static PlaceCount[] Places(IEnumerable<string?> locations) =>
        locations
            .Where(location => !string.IsNullOrWhiteSpace(location))
            .GroupBy(location => location!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new PlaceCount(group.First()!.Trim(), group.Count()))
            .OrderBy(place => place.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static RecommenderCount[] Recommenders(IEnumerable<string?> names) =>
        names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new RecommenderCount(group.First()!.Trim(), group.Count()))
            .OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static LoanCount[] Loans(IEnumerable<string?> names) =>
        Loans(names.Select(name => (name, (DateOnly?)null)), DateOnly.MaxValue);

    public static LoanCount[] Loans(IEnumerable<(string? Name, DateOnly? Due)> loans, DateOnly today) =>
        loans
            .Where(loan => !string.IsNullOrWhiteSpace(loan.Name))
            .GroupBy(loan => loan.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new LoanCount(
                group.First().Name!.Trim(),
                group.Count(),
                group.Count(loan => loan.Due is DateOnly due && due < today)))
            .OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static int ReadingStreak(IEnumerable<DateOnly> dates, DateOnly today)
    {
        var days = dates.Distinct().OrderByDescending(day => day).ToList();
        if (days.Count == 0 || days[0] < today.AddDays(-1))
        {
            return 0;
        }

        var streak = 1;
        for (var index = 1; index < days.Count; index++)
        {
            if (days[index - 1].DayNumber - days[index].DayNumber != 1)
            {
                break;
            }

            streak++;
        }

        return streak;
    }

    public static int[] ReadingDays(IEnumerable<DateOnly> dates, int year, int month) =>
        dates
            .Where(date => date.Year == year && date.Month == month)
            .Select(date => date.Day)
            .Distinct()
            .OrderBy(day => day)
            .ToArray();

    public static DayReading[] ReadingsOn(IEnumerable<Book> books, DateOnly day) =>
        books
            .SelectMany(book => book.Sessions
                .Where(session => session.Date == day)
                .Select(session => new DayReading(book.Id, book.Title, session.FromPage, session.ToPage)))
            .OrderBy(reading => reading.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reading => reading.BookId)
            .ToArray();

    public static IEnumerable<QuoteListItem> MatchingQuotes(IEnumerable<QuoteListItem> quotes, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return quotes;
        }

        var term = query.Trim();
        return quotes.Where(quote =>
            quote.Text.Contains(term, StringComparison.OrdinalIgnoreCase)
            || quote.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || quote.Author.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public static AuthorCount[] AuthorCounts(IEnumerable<string> authors) =>
        authors
            .GroupBy(name => name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AuthorCount(group.First(), group.Count()))
            .OrderBy(author => author.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
        BookFormat? format = null,
        string? series = null,
        string? place = null,
        string? recommendedBy = null,
        string? loanedTo = null)
    {
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            var isbn = NormalizeIsbn(q);
            books = isbn is null
                ? books.Where(book =>
                    book.Title.ToLower().Contains(term)
                    || book.Author.ToLower().Contains(term)
                    || (book.Subtitle != null && book.Subtitle.ToLower().Contains(term))
                    || (book.Series != null && book.Series.ToLower().Contains(term))
                    || (book.Publisher != null && book.Publisher.ToLower().Contains(term))
                    || (book.Isbn != null && book.Isbn.ToLower().Contains(term))
                    || (book.Notes != null && book.Notes.ToLower().Contains(term))
                    || (book.Review != null && book.Review.ToLower().Contains(term))
                    || (book.Location != null && book.Location.ToLower().Contains(term))
                    || (book.Translator != null && book.Translator.ToLower().Contains(term))
                    || (book.OriginalTitle != null && book.OriginalTitle.ToLower().Contains(term))
                    || (book.Inscription != null && book.Inscription.ToLower().Contains(term))
                    || (book.RecommendedBy != null && book.RecommendedBy.ToLower().Contains(term))
                    || book.Quotes.Any(quote => quote.Text.ToLower().Contains(term)))
                : books.Where(book =>
                    book.Title.ToLower().Contains(term)
                    || book.Author.ToLower().Contains(term)
                    || (book.Subtitle != null && book.Subtitle.ToLower().Contains(term))
                    || (book.Series != null && book.Series.ToLower().Contains(term))
                    || book.Isbn == isbn
                    || (book.Notes != null && book.Notes.ToLower().Contains(term))
                    || (book.Review != null && book.Review.ToLower().Contains(term))
                    || (book.Location != null && book.Location.ToLower().Contains(term))
                    || (book.Translator != null && book.Translator.ToLower().Contains(term))
                    || (book.OriginalTitle != null && book.OriginalTitle.ToLower().Contains(term))
                    || (book.Inscription != null && book.Inscription.ToLower().Contains(term))
                    || (book.RecommendedBy != null && book.RecommendedBy.ToLower().Contains(term))
                    || book.Quotes.Any(quote => quote.Text.ToLower().Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            var name = author.Trim().ToLower();
            books = books.Where(book => book.Author.ToLower() == name);
        }

        if (!string.IsNullOrWhiteSpace(series))
        {
            var name = series.Trim().ToLower();
            books = books.Where(book => book.Series != null && book.Series.ToLower() == name);
        }

        if (!string.IsNullOrWhiteSpace(place))
        {
            var name = place.Trim().ToLower();
            books = books.Where(book => book.Location != null && book.Location.ToLower() == name);
        }

        if (!string.IsNullOrWhiteSpace(recommendedBy))
        {
            var name = recommendedBy.Trim().ToLower();
            books = books.Where(book => book.RecommendedBy != null && book.RecommendedBy.ToLower() == name);
        }

        if (!string.IsNullOrWhiteSpace(loanedTo))
        {
            var name = loanedTo.Trim().ToLower();
            books = books.Where(book => book.LoanedTo != null && book.LoanedTo.ToLower() == name);
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

    public static int? ParsePublishYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        for (var index = 0; index <= value.Length - 4; index++)
        {
            if (!char.IsDigit(value[index]) || !char.IsDigit(value[index + 1]) || !char.IsDigit(value[index + 2]) || !char.IsDigit(value[index + 3]))
            {
                continue;
            }

            if (index > 0 && char.IsDigit(value[index - 1]))
            {
                continue;
            }

            if (index + 4 < value.Length && char.IsDigit(value[index + 4]))
            {
                continue;
            }

            var year = int.Parse(value.AsSpan(index, 4), System.Globalization.CultureInfo.InvariantCulture);
            if (year is >= 1000 and <= 2100)
            {
                return year;
            }
        }

        return null;
    }

    public static BookFormat? ParseFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.ToLowerInvariant();
        if (text.Contains("audio", StringComparison.Ordinal))
        {
            return BookFormat.Audiobook;
        }

        if (text.Contains("ebook", StringComparison.Ordinal) || text.Contains("electronic", StringComparison.Ordinal))
        {
            return BookFormat.Ebook;
        }

        if (text.Contains("hard", StringComparison.Ordinal))
        {
            return BookFormat.Hardcover;
        }

        if (text.Contains("paper", StringComparison.Ordinal))
        {
            return BookFormat.Paperback;
        }

        return null;
    }

    public static string? CleanSubtitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().Trim('"').Trim();
        if (text.Length is 0 or > 80
            || text.Contains('(') || text.Contains(')') || text.Contains('"') || text.Contains('“'))
        {
            return null;
        }

        var lower = text.ToLowerInvariant();
        if (lower.Contains("not official", StringComparison.Ordinal)
            || lower.StartsWith("drawings", StringComparison.Ordinal)
            || lower.StartsWith("illustrated", StringComparison.Ordinal)
            || lower.Contains("thematic", StringComparison.Ordinal))
        {
            return null;
        }

        return text;
    }

    public static string[] UsefulSubjects(IEnumerable<string?>? subjects)
    {
        if (subjects is null)
        {
            return [];
        }

        var tags = new List<string>();
        foreach (var subject in subjects.Take(10))
        {
            var name = CanonicalTag(subject);
            if (name is null
                || name.Length > MaxTagLength
                || name.Contains(':', StringComparison.Ordinal)
                || name.Contains("award", StringComparison.Ordinal)
                || name.Any(char.IsDigit)
                || name.StartsWith("bk.", StringComparison.Ordinal)
                || name.StartsWith("bk ", StringComparison.Ordinal)
                || name == "fiction"
                || (name.EndsWith(" fiction", StringComparison.Ordinal) && name != "science fiction")
                || !name.All(character => char.IsAsciiLetter(character) || character is ' ' or '-' or '\''))
            {
                continue;
            }

            if (tags.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            tags.Add(name);
        }

        string[] preferred = ["science fiction", "fantasy", "mystery", "horror", "poetry", "biography", "history", "romance", "thriller"];
        return tags
            .Select((name, index) => (name, index))
            .OrderBy(item => Array.IndexOf(preferred, item.name) is var rank and >= 0 ? rank : preferred.Length)
            .ThenBy(item => item.index)
            .Take(4)
            .Select(item => item.name)
            .ToArray();
    }

    public static EditionChoice? ChooseEdition(IEnumerable<EditionChoice> editions, string workTitle, int? typicalPages = null)
    {
        var candidates = editions.Where(edition => !string.IsNullOrWhiteSpace(edition.Publisher)).ToList();
        var english = candidates
            .Where(edition => string.Equals(edition.Language, "eng", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pool = english.Count > 0
            ? english
            : candidates.Where(edition => TitlesMatch(edition.Title, workTitle)).ToList();

        return pool
            .OrderBy(edition => edition.Year ?? int.MaxValue)
            .ThenByDescending(edition => TitlesMatch(edition.Title, workTitle))
            .ThenBy(edition => typicalPages is int typical && edition.Pages is int pages ? Math.Abs(pages - typical) : int.MaxValue)
            .ThenByDescending(edition => edition.Pages is not null)
            .FirstOrDefault();
    }

    public static bool FillEmpty(Book book, CatalogMatch match, List<string> tags)
    {
        var changed = false;

        void SetText(string? current, string? incoming, Action<string> apply, int max)
        {
            if (!string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(incoming))
            {
                return;
            }

            var trimmed = incoming.Trim();
            apply(trimmed.Length <= max ? trimmed : trimmed[..max]);
            changed = true;
        }

        if (book.Year is null && match.Year is >= 1000 and <= 2100)
        {
            book.Year = match.Year;
            changed = true;
        }

        if (book.Pages is null && match.Pages is >= 1 and <= 20000)
        {
            book.Pages = match.Pages;
            changed = true;
        }

        SetText(book.Publisher, match.Publisher, value => book.Publisher = value, 200);
        SetText(book.Language, match.Language, value => book.Language = value, 40);
        SetText(book.Subtitle, match.Subtitle, value => book.Subtitle = value, 200);

        if (string.IsNullOrWhiteSpace(book.CoverUrl) && match.CoverUrl is { } cover && cover.Length <= 500 && IsCoverUrl(cover))
        {
            book.CoverUrl = cover.Trim();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(book.Isbn) && NormalizeIsbn(match.Isbn) is { } isbn)
        {
            book.Isbn = isbn;
            changed = true;
        }

        if (book.Format is null && match.Format is { } format)
        {
            book.Format = format;
            changed = true;
        }

        foreach (var tag in UsefulSubjects(match.Tags))
        {
            if (tags.Count >= MaxTags || tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            tags.Add(tag);
            changed = true;
        }

        return changed;
    }

    private static bool TitlesMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var a = string.Join(' ', left.Trim().TrimEnd('.').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var b = string.Join(' ', right.Trim().TrimEnd('.').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
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
