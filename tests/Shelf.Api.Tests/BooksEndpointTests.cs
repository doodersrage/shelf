using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class BooksEndpointTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Home_page_lists_created_books()
    {
        var created = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest("The Dispossessed", "Ursula K. Le Guin", BookStatus.Finished, 5, 1974),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("The Dispossessed", html);
        Assert.Contains("Ursula K. Le Guin", html);
        Assert.Contains("Finished", html);
        Assert.Contains("1974", html);
        Assert.Contains("Add a book", html);
    }

    [Fact]
    public async Task Create_then_get_returns_the_book()
    {
        var book = await CreateAsync(new CreateBookRequest("Kindred", "Octavia E. Butler", BookStatus.Reading, null));
        Assert.Equal("Kindred", book.Title);
        Assert.Equal(BookStatus.Reading, book.Status);
        Assert.NotNull(book.StartedOn);

        var fetched = await _client.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions);
        Assert.Equal(book.Id, fetched?.Id);
        Assert.Equal("Octavia E. Butler", fetched?.Author);
    }

    [Fact]
    public async Task Create_rejects_an_empty_title()
    {
        var response = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest("", "Someone", BookStatus.Want, null),
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_a_title_of_spaces()
    {
        var response = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest("   ", "Someone", BookStatus.Want, null),
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_a_year_outside_range()
    {
        var response = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest("Undated", "Someone", BookStatus.Want, null, 999),
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_a_rating_above_five()
    {
        var response = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest("Too Highly Rated", "Someone", BookStatus.Finished, 9),
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_normalizes_isbn_and_tags()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "A Wizard of Earthsea",
            "Ursula K. Le Guin",
            BookStatus.Want,
            null,
            1968,
            Isbn: "0-306-40615-2",
            Pages: 205,
            Notes: "First of Earthsea",
            LoanedTo: "  Ged  ",
            Tags: ["Science Fiction", "science fiction", "  Novel  "]));

        Assert.Equal("0306406152", book.Isbn);
        Assert.Equal(205, book.Pages);
        Assert.Equal("First of Earthsea", book.Notes);
        Assert.Equal("Ged", book.LoanedTo);
        Assert.Equal(["novel", "science fiction"], book.Tags);
        Assert.Empty(book.Quotes);
    }

    [Fact]
    public async Task Create_rejects_a_bad_isbn_and_a_date_range()
    {
        var isbn = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest("Bad Isbn", "Someone", BookStatus.Want, null, Isbn: "123"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, isbn.StatusCode);

        var dates = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest(
                "Backwards",
                "Someone",
                BookStatus.Finished,
                null,
                StartedOn: new DateOnly(2020, 5, 2),
                FinishedOn: new DateOnly(2020, 5, 1)),
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, dates.StatusCode);

        var page = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest("Too Far", "Someone", BookStatus.Reading, null, Pages: 100, CurrentPage: 101),
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
    }

    [Fact]
    public async Task Get_missing_book_returns_not_found()
    {
        var response = await _client.GetAsync("/books/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_and_delete_round_trip()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "Piranesi",
            "Susanna Clarke",
            BookStatus.Want,
            null,
            Tags: ["novel"]));

        var updated = await _client.PutAsJsonAsync(
            $"/books/{book.Id}",
            new UpdateBookRequest("Piranesi", "Susanna Clarke", BookStatus.Finished, 5, 2020, Tags: ["favorite"]),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var afterUpdate = await updated.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.NotNull(afterUpdate);
        Assert.Equal(BookStatus.Finished, afterUpdate.Status);
        Assert.Equal(5, afterUpdate.Rating);
        Assert.Equal(2020, afterUpdate.Year);
        Assert.Equal(["favorite"], afterUpdate.Tags);
        Assert.NotNull(afterUpdate.FinishedOn);

        var listed = await _client.GetFromJsonAsync<BookResponse[]>("/books?tag=favorite", JsonOptions);
        Assert.Contains(listed!, item => item.Id == book.Id);

        var deleted = await _client.DeleteAsync($"/books/{book.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var missing = await _client.GetAsync($"/books/{book.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var afterDelete = await _client.GetFromJsonAsync<BookResponse[]>("/books?tag=favorite", JsonOptions);
        Assert.DoesNotContain(afterDelete!, item => item.Id == book.Id);
    }

    [Fact]
    public async Task List_filters_by_query_status_and_sort()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        await CreateAsync(new CreateBookRequest($"{marker} Later", "Ada", BookStatus.Abandoned, null, 1990));
        var newer = await CreateAsync(new CreateBookRequest($"{marker} Earlier", "Bea", BookStatus.Want, 4, 2010));

        var byYear = await _client.GetFromJsonAsync<BookResponse[]>(
            $"/books?q={marker}&sort=year",
            JsonOptions);
        Assert.Equal([$"{marker} Earlier", $"{marker} Later"], byYear!.Select(book => book.Title).ToArray());

        var abandoned = await _client.GetFromJsonAsync<BookResponse[]>(
            $"/books?q={marker}&status=Abandoned",
            JsonOptions);
        Assert.Equal([$"{marker} Later"], abandoned!.Select(book => book.Title).ToArray());

        var byAuthor = await _client.GetFromJsonAsync<BookResponse[]>(
            $"/books?q={Uri.EscapeDataString(newer.Author)}&sort=title",
            JsonOptions);
        Assert.Contains(byAuthor!, book => book.Id == newer.Id);

        var byAdded = await _client.GetFromJsonAsync<BookResponse[]>(
            $"/books?q={marker}&sort=added",
            JsonOptions);
        Assert.NotNull(byAdded);
        Assert.Equal(2, byAdded.Length);
        Assert.True(byAdded[0].AddedAt >= byAdded[1].AddedAt);
    }

    [Fact]
    public async Task Quotes_round_trip_and_search()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "Parable of the Sower",
            "Octavia E. Butler",
            BookStatus.Reading,
            null,
            Pages: 300));

        var created = await _client.PostAsJsonAsync(
            $"/books/{book.Id}/quotes",
            new CreateQuoteRequest("All that you touch you change.", 3),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var quote = await created.Content.ReadFromJsonAsync<QuoteResponse>(JsonOptions);
        Assert.NotNull(quote);
        Assert.Equal(3, quote.Page);

        var tooFar = await _client.PostAsJsonAsync(
            $"/books/{book.Id}/quotes",
            new CreateQuoteRequest("Past the last page.", 400),
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, tooFar.StatusCode);

        var found = await _client.GetFromJsonAsync<BookResponse[]>(
            "/books?q=All%20that%20you%20touch",
            JsonOptions);
        Assert.Contains(found!, item => item.Id == book.Id);

        var deleted = await _client.DeleteAsync($"/books/{book.Id}/quotes/{quote.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var quotes = await _client.GetFromJsonAsync<QuoteResponse[]>($"/books/{book.Id}/quotes", JsonOptions);
        Assert.Empty(quotes!);

        var missing = await _client.DeleteAsync($"/books/{book.Id}/quotes/{quote.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Stats_include_finished_pages_and_this_year()
    {
        var before = await _client.GetFromJsonAsync<ShelfStatsResponse>("/books/stats", JsonOptions);
        Assert.NotNull(before);

        await CreateAsync(new CreateBookRequest(
            "Finished Pages",
            "Someone",
            BookStatus.Finished,
            5,
            Pages: 120,
            Tags: ["counted"]));

        var after = await _client.GetFromJsonAsync<ShelfStatsResponse>("/books/stats", JsonOptions);
        Assert.NotNull(after);
        Assert.Equal(before.Total + 1, after.Total);
        Assert.Equal(before.Finished + 1, after.Finished);
        Assert.Equal(before.PagesRead + 120, after.PagesRead);
        Assert.Equal(before.FinishedThisYear + 1, after.FinishedThisYear);
        Assert.Contains(after.Tags, tag => tag.Name == "counted");
    }

    [Fact]
    public async Task Library_and_stats_pages_render()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "The Tombs of Atuan",
            "Ursula K. Le Guin",
            BookStatus.Want,
            null,
            Notes: "Second of Earthsea"));

        var library = await _client.GetAsync($"/library/{book.Id}");
        Assert.Equal(HttpStatusCode.OK, library.StatusCode);
        var libraryHtml = await library.Content.ReadAsStringAsync();
        Assert.Contains("The Tombs of Atuan", libraryHtml);
        Assert.Contains("Second of Earthsea", libraryHtml);
        Assert.Contains("Add quote", libraryHtml);

        var missing = await _client.GetAsync("/library/999999");
        var missingHtml = await missing.Content.ReadAsStringAsync();
        Assert.Contains("Not on the shelf", missingHtml);

        var stats = await _client.GetAsync("/stats");
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        var statsHtml = await stats.Content.ReadAsStringAsync();
        Assert.Contains("Pages read", statsHtml);
        Assert.Contains("Finished this year", statsHtml);
    }

    [Fact]
    public async Task Catalog_fields_round_trip_and_reject_a_broken_cover()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "The Tombs of Atuan",
            "Ursula K. Le Guin",
            BookStatus.Want,
            null,
            1971,
            Subtitle: "The Earthsea Cycle",
            Publisher: "Atheneum",
            Language: "English",
            Format: BookFormat.Hardcover,
            Series: "Earthsea",
            SeriesNumber: 2,
            Loved: true));

        Assert.Equal("The Earthsea Cycle", book.Subtitle);
        Assert.Equal(BookFormat.Hardcover, book.Format);
        Assert.Equal("Earthsea", book.Series);
        Assert.Equal(2, book.SeriesNumber);
        Assert.True(book.Loved);
        Assert.Empty(book.Sessions);

        var numbered = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest("Orphan Volume", "Someone", BookStatus.Want, null, SeriesNumber: 3),
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, numbered.StatusCode);

        var cover = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest("No Cover", "Someone", BookStatus.Want, null, CoverUrl: "ftp://covers.example/a.jpg"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, cover.StatusCode);

        var byAuthor = await _client.GetFromJsonAsync<BookResponse[]>(
            "/books?author=ursula%20k.%20le%20guin&loved=true",
            JsonOptions);
        Assert.Contains(byAuthor!, item => item.Id == book.Id);
    }

    [Fact]
    public async Task Session_advances_the_current_page()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "The Farthest Shore",
            "Ursula K. Le Guin",
            BookStatus.Reading,
            null,
            Pages: 200,
            CurrentPage: 10));

        var created = await _client.PostAsJsonAsync(
            $"/books/{book.Id}/sessions",
            new CreateSessionRequest(new DateOnly(2026, 10, 1), 10, 40, "Evening"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var backwards = await _client.PostAsJsonAsync(
            $"/books/{book.Id}/sessions",
            new CreateSessionRequest(null, 50, 20, null),
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);

        var fetched = await _client.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions);
        Assert.Equal(40, fetched?.CurrentPage);
        Assert.Equal("Evening", fetched?.Sessions.Single().Note);
    }

    [Fact]
    public async Task Export_and_import_skip_books_already_on_the_shelf()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "Tehanu",
            "Ursula K. Le Guin",
            BookStatus.Finished,
            5,
            Isbn: "978-0-689-31595-4",
            Series: "Earthsea",
            SeriesNumber: 4));
        await _client.PostAsJsonAsync(
            $"/books/{book.Id}/quotes",
            new CreateQuoteRequest("A hawk in a cage.", 12),
            JsonOptions);

        var export = await _client.GetFromJsonAsync<LibraryExport>("/books/export", JsonOptions);
        Assert.NotNull(export);
        Assert.Contains(export.Books, item => item.Id == book.Id && item.Quotes.Length == 1);

        var imported = await _client.PostAsJsonAsync("/books/import", export, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var result = await imported.Content.ReadFromJsonAsync<ImportResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(0, result.Added);
        Assert.True(result.Skipped >= 1);

        await _client.PutAsJsonAsync("/settings", new UpdateSettingsRequest(0), JsonOptions);
        export = export with
        {
            YearlyGoal = 12,
            Books =
            [
                export.Books.Single(item => item.Id == book.Id) with
                {
                    Id = 0,
                    Title = "Tales from Earthsea",
                    Isbn = null,
                    Quotes = [],
                    Sessions = [],
                },
            ],
        };
        var second = await _client.PostAsJsonAsync("/books/import", export, JsonOptions);
        var secondResult = await second.Content.ReadFromJsonAsync<ImportResult>(JsonOptions);
        Assert.Equal(1, secondResult?.Added);

        var goal = await _client.GetFromJsonAsync<ShelfSettingsResponse>("/settings", JsonOptions);
        Assert.Equal(12, goal?.YearlyGoal);
    }

    [Fact]
    public async Task Quotes_page_lists_a_saved_quote()
    {
        var book = await CreateAsync(new CreateBookRequest("The Other Wind", "Ursula K. Le Guin", BookStatus.Want, null));
        await _client.PostAsJsonAsync(
            $"/books/{book.Id}/quotes",
            new CreateQuoteRequest("The wind was from the west.", 1),
            JsonOptions);

        var page = await _client.GetAsync("/quotes");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("The wind was from the west.", html);
        Assert.Contains("The Other Wind", html);

        var listed = await _client.GetFromJsonAsync<QuoteListItem[]>("/books/quotes", JsonOptions);
        Assert.Contains(listed!, quote => quote.BookId == book.Id);
    }

    [Fact]
    public async Task Lookup_returns_a_catalog_match_for_a_known_isbn()
    {
        var match = await _client.GetFromJsonAsync<CatalogMatch>("/books/lookup?isbn=978-0-441-47812-5", JsonOptions);
        Assert.Equal("The Left Hand of Darkness", match?.Title);
        Assert.Equal("Ursula K. Le Guin", match?.Author);
        Assert.Equal(1969, match?.Year);
        Assert.Equal(304, match?.Pages);
        Assert.Equal("English", match?.Language);

        var missing = await _client.GetAsync("/books/lookup?isbn=not-an-isbn");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var unknown = await _client.GetAsync("/books/lookup?isbn=9780000000002");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Returning_a_book_clears_the_loan()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "The Tombs of Atuan",
            "Ursula K. Le Guin",
            BookStatus.Reading,
            null,
            LoanedTo: "Tenar"));
        Assert.Equal("Tenar", book.LoanedTo);
        Assert.NotNull(book.LoanedOn);

        var returned = await _client.PostAsync($"/books/{book.Id}/return", null);
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        var body = await returned.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.Null(body?.LoanedTo);
        Assert.Null(body?.LoanedOn);

        var missing = await _client.PostAsync("/books/999999/return", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public void Pace_counts_pages_across_the_log()
    {
        var pace = BookRules.PagesPerDay(
        [
            new ReadingSession { Date = new DateOnly(2026, 1, 1), FromPage = 1, ToPage = 20 },
            new ReadingSession { Date = new DateOnly(2026, 1, 3), FromPage = 21, ToPage = 40 },
        ]);
        Assert.Equal(19, pace);
        Assert.Null(BookRules.PagesPerDay([]));

        Assert.True(BookRules.IsSameCopy("978-0-441-47812-5", "Other", "Other", "9780441478125", "Different", "Person"));
        Assert.True(BookRules.IsSameCopy(null, "Kindred", "Octavia E. Butler", null, "kindred", "octavia e. butler"));
        Assert.False(BookRules.IsSameCopy(null, "", "", null, "Kindred", "Octavia E. Butler"));
    }

    [Fact]
    public async Task Place_and_translator_round_trip_and_can_be_searched()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "The Dispossessed",
            "Ursula K. Le Guin",
            BookStatus.Reading,
            null,
            Pages: 387,
            CurrentPage: 40,
            Location: "  North wall  ",
            AcquiredOn: new DateOnly(2020, 5, 1),
            Translator: "   "));

        Assert.Equal("North wall", book.Location);
        Assert.Equal(new DateOnly(2020, 5, 1), book.AcquiredOn);
        Assert.Null(book.Translator);

        var found = await _client.GetFromJsonAsync<BookResponse[]>("/books?q=north%20wall", JsonOptions);
        Assert.Contains(found!, item => item.Id == book.Id);
    }

    [Fact]
    public async Task Pick_and_authors_follow_the_shelf()
    {
        await CreateAsync(new CreateBookRequest("City of Illusions", "Ursula K. Le Guin", BookStatus.Want, null));
        var listed = await _client.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions);
        var shelf = listed!.Select(item => new Book
        {
            Id = item.Id,
            Title = item.Title,
            Author = item.Author,
            Status = item.Status,
        }).ToList();
        var expected = BookRules.Pick(shelf, BookStatus.Want, DateOnly.FromDateTime(DateTime.UtcNow));

        var pick = await _client.GetFromJsonAsync<BookResponse>("/books/pick", JsonOptions);
        Assert.Equal(expected?.Id, pick?.Id);

        var authors = await _client.GetFromJsonAsync<AuthorCount[]>("/books/authors", JsonOptions);
        Assert.Contains(authors!, author => author.Name == "Ursula K. Le Guin" && author.Count >= 1);

        var page = await _client.GetAsync("/authors");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Ursula K. Le Guin", html);
        Assert.Contains("Authors", html);
    }

    [Fact]
    public void Remaining_pages_use_the_reading_pace()
    {
        var book = new Book
        {
            Title = "The Dispossessed",
            Author = "Ursula K. Le Guin",
            Status = BookStatus.Reading,
            Pages = 100,
            CurrentPage = 20,
            Sessions =
            [
                new ReadingSession { Date = new DateOnly(2026, 1, 1), FromPage = 1, ToPage = 21 },
            ],
        };
        Assert.Equal(80, BookRules.PagesRemaining(book));
        Assert.Equal(4, BookRules.DaysRemaining(book));

        var wanted = new List<Book>
        {
            new() { Id = 1, Title = "A", Author = "A", Status = BookStatus.Want },
            new() { Id = 2, Title = "B", Author = "B", Status = BookStatus.Want },
        };
        Assert.Equal(1, BookRules.Pick(wanted, BookStatus.Want, DateOnly.FromDayNumber(2))?.Id);
        Assert.Equal(2, BookRules.Pick(wanted, BookStatus.Want, DateOnly.FromDayNumber(3))?.Id);
        Assert.Null(BookRules.Pick(wanted, BookStatus.Finished, DateOnly.FromDayNumber(2)));

        var counts = BookRules.AuthorCounts(["Ursula K. Le Guin", "ursula k. le guin", "Octavia E. Butler"]);
        Assert.Equal(2, counts.Single(author => author.Name == "Ursula K. Le Guin").Count);
    }

    [Fact]
    public async Task Series_lists_books_in_reading_order()
    {
        var first = await CreateAsync(new CreateBookRequest(
            "A Wizard of Earthsea", "Ursula K. Le Guin", BookStatus.Finished, 5, Series: "Earthsea", SeriesNumber: 1));
        var third = await CreateAsync(new CreateBookRequest(
            "The Farthest Shore", "Ursula K. Le Guin", BookStatus.Want, null, Series: "earthsea", SeriesNumber: 3));
        var second = await CreateAsync(new CreateBookRequest(
            "The Tombs of Atuan", "Ursula K. Le Guin", BookStatus.Reading, null, Series: "Earthsea", SeriesNumber: 2));

        var shelves = await _client.GetFromJsonAsync<SeriesShelf[]>("/books/series", JsonOptions);
        var earthsea = shelves!.Single(shelf => shelf.Books.Any(book => book.Id == second.Id));
        var order = earthsea.Books.Select(book => book.Id).ToList();
        Assert.True(order.IndexOf(first.Id) < order.IndexOf(second.Id));
        Assert.True(order.IndexOf(second.Id) < order.IndexOf(third.Id));

        var filtered = await _client.GetFromJsonAsync<BookResponse[]>("/books?series=earthsea", JsonOptions);
        Assert.Contains(filtered!, book => book.Id == second.Id);

        var page = await _client.GetAsync("/series");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("The Tombs of Atuan", html);
        Assert.Contains("Earthsea", html);
    }

    [Fact]
    public void Series_shelves_keep_one_name_and_put_unnumbered_books_last()
    {
        var shelves = BookRules.SeriesShelves(
        [
            new Book { Id = 2, Title = "The Tombs of Atuan", Author = "Ursula K. Le Guin", Series = "earthsea", SeriesNumber = 2, Status = BookStatus.Want },
            new Book { Id = 1, Title = "A Wizard of Earthsea", Author = "Ursula K. Le Guin", Series = "Earthsea", SeriesNumber = 1, Status = BookStatus.Want },
            new Book { Id = 3, Title = "Tales from Earthsea", Author = "Ursula K. Le Guin", Series = "Earthsea", Status = BookStatus.Want },
        ]);

        var earthsea = Assert.Single(shelves);
        Assert.Equal("earthsea", earthsea.Name);
        Assert.Equal(["A Wizard of Earthsea", "The Tombs of Atuan", "Tales from Earthsea"], earthsea.Books.Select(book => book.Title));
    }

    [Fact]
    public async Task Goal_can_be_replaced()
    {
        var updated = await _client.PutAsJsonAsync("/settings", new UpdateSettingsRequest(24), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var stats = await _client.GetFromJsonAsync<ShelfStatsResponse>("/books/stats", JsonOptions);
        Assert.Equal(24, stats?.YearlyGoal);
    }

    private async Task<BookResponse> CreateAsync(CreateBookRequest request)
    {
        var response = await _client.PostAsJsonAsync("/books", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var book = await response.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.NotNull(book);
        return book;
    }
}

public sealed class ShelfApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"shelf-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Shelf", $"Data Source={_databasePath}");
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services => services.AddSingleton<IBookLookup, StubBookLookup>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}

file sealed class StubBookLookup : IBookLookup
{
    public Task<CatalogMatch?> FindAsync(string isbn, CancellationToken cancellationToken)
    {
        if (BookRules.NormalizeIsbn(isbn) != "9780441478125")
        {
            return Task.FromResult<CatalogMatch?>(null);
        }

        return Task.FromResult<CatalogMatch?>(new CatalogMatch(
            "The Left Hand of Darkness",
            "Ursula K. Le Guin",
            1969,
            304,
            "Ace Books",
            "English",
            "https://covers.openlibrary.org/b/id/1-M.jpg"));
    }
}
