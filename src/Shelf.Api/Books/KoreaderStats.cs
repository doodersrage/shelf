using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record KoreaderStatsImport(int Books, int Days, int Seconds, IReadOnlyList<string> NotFound);

// KOReader's reading statistics (statistics.sqlite3, from its settings folder): the time spent on each page, which
// becomes time read on each day for the books on this shelf. Bringing the file again replaces what it brought before,
// so it can be brought as often as you like.
public static class KoreaderStats
{
    public const long MaxBytes = 100L * 1024 * 1024;

    // KOReader counts a page for at most a few minutes; a day cannot hold more than a day.
    private const int MostADay = 24 * 60 * 60;

    public sealed record BookTime(string Title, string Authors, string? Digest, IReadOnlyDictionary<DateOnly, int> Days);

    // The books in the file and the seconds read on each, by day in UTC as the shelf counts days. Null when the file is
    // not KOReader's statistics.
    public static IReadOnlyList<BookTime>? Read(string path)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT b.id, b.title, b.authors, b.md5, p.start_time, p.duration
                FROM page_stat_data p JOIN book b ON b.id = p.id_book
                WHERE p.duration > 0
                """;
            var books = new Dictionary<long, (string Title, string Authors, string? Digest, Dictionary<DateOnly, int> Days)>();
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var id = rows.GetInt64(0);
                if (!books.TryGetValue(id, out var book))
                {
                    book = (rows.IsDBNull(1) ? "" : rows.GetString(1), rows.IsDBNull(2) ? "" : rows.GetString(2), rows.IsDBNull(3) ? null : rows.GetString(3), []);
                    books[id] = book;
                }

                var day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(rows.GetInt64(4)).UtcDateTime);
                book.Days[day] = Math.Min(MostADay, book.Days.GetValueOrDefault(day) + (int)Math.Min(rows.GetInt64(5), MostADay));
            }

            return books.Values.Select(book => new BookTime(book.Title, book.Authors, book.Digest, book.Days)).ToList();
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public static async Task<KoreaderStatsImport> ImportAsync(ShelfDb db, IReadOnlyList<BookTime> times, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        // The reader's own books, and books lent to them, which KOReader may have read too.
        var books = await db.Books.IgnoreQueryFilters().AsNoTracking()
            .Where(book => book.OwnerId == me || book.BorrowerId == me)
            .Select(book => new { book.Id, book.Title, book.Subtitle, book.Author, book.KoreaderDigest })
            .ToListAsync(cancellationToken);
        var notFound = new List<string>();
        var matched = new HashSet<int>();
        int days = 0, seconds = 0;
        foreach (var time in times.Where(time => time.Days.Count > 0))
        {
            // KOReader writes authors one to a line.
            var author = time.Authors.Replace('\n', ' ');
            var byTitle = books.Where(book => KindleClippings.SameTitle(book.Title, book.Subtitle, time.Title)).ToList();
            var book = books.FirstOrDefault(book => time.Digest is { Length: 32 } && string.Equals(book.KoreaderDigest, time.Digest, StringComparison.OrdinalIgnoreCase))
                ?? byTitle.FirstOrDefault(book => KindleClippings.SameAuthor(book.Author, author))
                ?? (byTitle.Count == 1 ? byTitle[0] : null);
            if (book is null)
            {
                notFound.Add(time.Title.Length > 0 ? time.Title : T("A book with no title"));
                continue;
            }

            matched.Add(book.Id);
            var rows = await db.ReadingDays.IgnoreQueryFilters().Where(day => day.BookId == book.Id && day.ReaderId == me).ToListAsync(cancellationToken);
            foreach (var (day, spent) in time.Days)
            {
                var row = rows.FirstOrDefault(item => item.Day == day);
                if (row is null)
                {
                    db.ReadingDays.Add(new ReadingDay { BookId = book.Id, ReaderId = me, Day = day, DeviceSeconds = spent });
                }
                else
                {
                    row.DeviceSeconds = spent;
                }

                days++;
                seconds += spent;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (matched.Count > 0)
        {
            await Audit.NoteAsync(db, Say("Brought reading time from KOReader"), detail: matched.Count.ToString(CultureInfo.InvariantCulture), cancellationToken: cancellationToken);
        }

        return new KoreaderStatsImport(matched.Count, days, seconds, notFound.Distinct().ToList());
    }

    // The file comes to a temporary place, since SQLite reads from a file.
    public static async Task<KoreaderStatsImport?> ImportAsync(ShelfDb db, Stream file, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"shelf-koreader-{Guid.NewGuid():N}.sqlite3");
        try
        {
            await using (var copy = File.Create(path))
            {
                await file.CopyToAsync(copy, cancellationToken);
            }

            return Read(path) is { } times ? await ImportAsync(db, times, cancellationToken) : null;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    public static async Task<IResult> Upload(IFormFile? file, ShelfDb db, CancellationToken cancellationToken)
    {
        if (file is null || file.Length is 0 or > MaxBytes)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [T("Choose statistics.sqlite3 from KOReader's settings folder, of up to 100 MB.")] });
        }

        await using var stream = file.OpenReadStream();
        return await ImportAsync(db, stream, cancellationToken) is { } imported
            ? TypedResults.Ok(imported)
            : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [T("That is not KOReader's statistics.sqlite3.")] });
    }
}
