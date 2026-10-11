using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class KoreaderStatsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Koreaders_page_times_become_reading_time_by_day_and_bringing_them_again_replaces_them()
    {
        var reader = await factory.SignUpAsync("KOReader Statistician");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("The Dispossessed", "Ursula K. Le Guin", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        var today = DateTimeOffset.UtcNow.Date;
        var yesterday = new DateTimeOffset(today.AddDays(-1), TimeSpan.Zero);
        var path = Statistics(
            ("The Dispossessed", "Ursula K. Le Guin", [(yesterday.AddHours(9), 600), (yesterday.AddHours(9.5), 300), (new DateTimeOffset(today, TimeSpan.Zero).AddHours(1), 120)]),
            ("Not Here", "Nobody", [(yesterday, 60)]));
        try
        {
            var first = await UploadAsync(reader, path);
            Assert.Equal((1, 2, 1020), (first.Books, first.Days, first.Seconds));
            Assert.Equal(["Not Here"], first.NotFound);

            // Time read in the browser that day stays, beside KOReader's.
            await reader.PostAsJsonAsync($"/books/{book.Id}/reading-time", new ReadingTimeRequest(null, 60), JsonOptions);
            var again = await UploadAsync(reader, path);
            Assert.Equal(1020, again.Seconds);
            var summary = await reader.GetFromJsonAsync<ListeningSummary>("/books/reading-time", JsonOptions);
            Assert.Equal((180, 1080), (summary!.TodaySeconds, summary.TotalSeconds));
            Assert.Equal(900, summary.LastDays[^2].Seconds);
        }
        finally
        {
            File.Delete(path);
        }

        var wrong = new MultipartFormDataContent { { new ByteArrayContent("not sqlite"u8.ToArray()), "file", "statistics.sqlite3" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsync("/books/reading-time/koreader", wrong)).StatusCode);
    }

    private static async Task<KoreaderStatsImport> UploadAsync(HttpClient reader, string path)
    {
        using var content = new MultipartFormDataContent { { new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", "statistics.sqlite3" } };
        var response = await reader.PostAsync("/books/reading-time/koreader", content);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<KoreaderStatsImport>(JsonOptions))!;
    }

    // A statistics.sqlite3 the way KOReader's statistics plugin writes it.
    private static string Statistics(params (string Title, string Authors, (DateTimeOffset Start, int Duration)[] Pages)[] books)
    {
        var path = Path.Combine(Path.GetTempPath(), $"shelf-stats-{Guid.NewGuid():N}.sqlite3");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE book (id integer PRIMARY KEY autoincrement, title text, authors text, notes integer, last_open integer, highlights integer, pages integer, series text, language text, md5 text, total_read_time integer, total_read_pages integer);
                CREATE TABLE page_stat_data (id_book integer, page integer NOT NULL DEFAULT 0, start_time integer NOT NULL DEFAULT 0, duration integer NOT NULL DEFAULT 0, total_pages integer NOT NULL DEFAULT 0, UNIQUE (id_book, page, start_time), FOREIGN KEY(id_book) REFERENCES book(id));
                """;
            create.ExecuteNonQuery();
            foreach (var (title, authors, pages) in books)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO book (title, authors, md5) VALUES ($title, $authors, 'abcdefabcdefabcdefabcdefabcdefab'); SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("$title", title);
                insert.Parameters.AddWithValue("$authors", authors);
                var id = (long)insert.ExecuteScalar()!;
                var page = 1;
                foreach (var (start, duration) in pages)
                {
                    using var stat = connection.CreateCommand();
                    stat.CommandText = "INSERT INTO page_stat_data (id_book, page, start_time, duration, total_pages) VALUES ($id, $page, $start, $duration, 300)";
                    stat.Parameters.AddWithValue("$id", id);
                    stat.Parameters.AddWithValue("$page", page++);
                    stat.Parameters.AddWithValue("$start", start.ToUnixTimeSeconds());
                    stat.Parameters.AddWithValue("$duration", duration);
                    stat.ExecuteNonQuery();
                }
            }
        }

        return path;
    }
}
