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

    private readonly HttpClient _client = factory.Client;

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

        var byTitle = await _client.GetFromJsonAsync<CatalogMatch>(
            "/books/lookup?title=A%20Wizard%20of%20Earthsea&author=Ursula%20K.%20Le%20Guin",
            JsonOptions);
        Assert.Equal("Parnassus Press", byTitle?.Publisher);
        Assert.Equal(205, byTitle?.Pages);
        Assert.Equal(BookFormat.Hardcover, byTitle?.Format);
    }

    [Fact]
    public async Task An_inscription_is_kept_and_can_be_searched()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "The Lathe of Heaven",
            "Ursula K. Le Guin",
            BookStatus.Want,
            null,
            Inscription: "  For the one who remembers the dream.  "));
        Assert.Equal("For the one who remembers the dream.", book.Inscription);

        var found = await _client.GetFromJsonAsync<BookResponse[]>("/books?q=remembers%20the%20dream", JsonOptions);
        Assert.Contains(found!, item => item.Id == book.Id);

        var page = await _client.GetAsync($"/library/{book.Id}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("For the one who remembers the dream.", html);
        Assert.Contains("inscription", html);
    }

    [Fact]
    public async Task The_library_shows_covers_and_what_is_being_read()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "The Lathe of Heaven",
            "Ursula K. Le Guin",
            BookStatus.Reading,
            null,
            CoverUrl: "https://covers.openlibrary.org/b/id/9-M.jpg"));

        var page = await _client.GetAsync("/");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("https://covers.openlibrary.org/b/id/9-M.jpg", html);
        Assert.Contains("Reading now", html);
        Assert.Contains("The Lathe of Heaven", html);
        Assert.Contains($"/library/{book.Id}", html);
    }

    [Fact]
    public async Task Enrich_fills_empty_catalog_fields_from_a_title()
    {
        var book = await CreateAsync(new CreateBookRequest("A Wizard of Earthsea", "Ursula K. Le Guin", BookStatus.Want, null));
        Assert.Null(book.Pages);

        var enriched = await _client.PostAsync($"/books/{book.Id}/enrich", null);
        Assert.Equal(HttpStatusCode.OK, enriched.StatusCode);
        var filled = await enriched.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.Equal(1968, filled?.Year);
        Assert.Equal(205, filled?.Pages);
        Assert.Equal("Parnassus Press", filled?.Publisher);
        Assert.Equal("English", filled?.Language);
        Assert.Equal(BookFormat.Hardcover, filled?.Format);
        Assert.Contains("fantasy", filled!.Tags);

        var again = await _client.PostAsync($"/books/{book.Id}/enrich", null);
        var second = await again.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.Equal(filled.Publisher, second?.Publisher);
        Assert.Equal(filled.Tags, second?.Tags);
    }

    [Fact]
    public void The_earliest_english_edition_is_kept()
    {
        var chosen = BookRules.ChooseEdition(
            [
                new EditionChoice("Yerdeniz", "Metis", 2016, 192, null, "paperback", null, null, null),
                new EditionChoice("A Wizard of Earthsea", "Bantam Books", 1975, 183, "eng", "Paperback", 2, null, null),
                new EditionChoice("The wizard of Earthsea", "Ace Pub. Co.", 1968, 205, "eng", null, 3, null, null),
                new EditionChoice("A wizard of Earthsea", "Parnassus Press", 1968, 205, "eng", null, 4, null, "9780000000000"),
            ],
            "A Wizard of Earthsea");
        Assert.Equal("Parnassus Press", chosen?.Publisher);
        Assert.Equal(1968, BookRules.ParsePublishYear("1968"));
        Assert.Equal(2012, BookRules.ParsePublishYear("Sep 11, 2012"));
        Assert.Equal(BookFormat.Paperback, BookRules.ParseFormat("Mass Market Paperback"));
        Assert.Equal(BookFormat.Hardcover, BookRules.ParseFormat("Hardcover"));
        Assert.Null(BookRules.ParseFormat("library binding"));
        Assert.Equal(["science fiction", "fantasy"], BookRules.UsefulSubjects(
            ["award:hugo_award=1970", "Fiction", "Fantasy", "Bk. 1.", "magic in fiction", "science fiction", "Hugo Award Winner"]));
        Assert.Null(BookRules.CleanSubtitle("\" A Tale of Magic and Shadow\" ( more thematic, not official)"));
        Assert.Null(BookRules.CleanSubtitle("Drawings by Ruth Robbins."));

        var closer = BookRules.ChooseEdition(
            [
                new EditionChoice("The Left Hand of Darkness", "Walker", 1969, 230, "eng", null, null, null, null),
                new EditionChoice("The left hand of darkness", "Ace Books", 1969, 304, "eng", null, null, null, null),
            ],
            "The Left Hand of Darkness",
            304);
        Assert.Equal("Ace Books", closer?.Publisher);
    }

    [Fact]
    public async Task Returning_a_book_clears_the_loan()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "The Tombs of Atuan",
            "Ursula K. Le Guin",
            BookStatus.Reading,
            null,
            LoanedTo: "Tenar",
            DueOn: new DateOnly(2026, 10, 1)));
        Assert.Equal("Tenar", book.LoanedTo);
        Assert.NotNull(book.LoanedOn);
        Assert.Equal(new DateOnly(2026, 10, 1), book.DueOn);

        var returned = await _client.PostAsync($"/books/{book.Id}/return", null);
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        var body = await returned.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.Null(body?.LoanedTo);
        Assert.Null(body?.LoanedOn);
        Assert.Null(body?.DueOn);

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

        wanted[1].Queued = true;
        Assert.Equal(2, BookRules.Pick(wanted, BookStatus.Want, DateOnly.FromDayNumber(2))?.Id);

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
    public async Task Stats_lists_books_finished_this_year_and_recent_sessions()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "The Lathe of Heaven", "Ursula K. Le Guin", BookStatus.Finished, 5));
        await _client.PostAsJsonAsync(
            $"/books/{book.Id}/sessions",
            new CreateSessionRequest(new DateOnly(2026, 10, 2), 1, 40, null),
            JsonOptions);

        var page = await _client.GetAsync("/stats");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("The Lathe of Heaven", html);
        Assert.Contains("Finished this year", html);
        Assert.Contains("Lately", html);
        Assert.Contains("1–40", html);
    }

    [Fact]
    public void Finished_books_stay_inside_their_year()
    {
        var books = new[]
        {
            new Book { Id = 1, Title = "Older", Author = "A", Status = BookStatus.Finished, FinishedOn = new DateOnly(2025, 12, 31) },
            new Book { Id = 2, Title = "Newer", Author = "A", Status = BookStatus.Finished, FinishedOn = new DateOnly(2026, 1, 2) },
            new Book { Id = 3, Title = "Still reading", Author = "A", Status = BookStatus.Reading, FinishedOn = new DateOnly(2026, 1, 3) },
        };
        Assert.Equal(["Newer"], BookRules.FinishedInYear(books, 2026).Select(book => book.Title));

        var sessions = BookRules.RecentSessions(
        [
            new Book
            {
                Id = 9,
                Title = "Later",
                Author = "A",
                Sessions = [new ReadingSession { Date = new DateOnly(2026, 2, 2), ToPage = 10 }],
            },
            new Book
            {
                Id = 4,
                Title = "Earlier",
                Author = "A",
                Sessions = [new ReadingSession { Date = new DateOnly(2026, 2, 1), FromPage = 1, ToPage = 5 }],
            },
        ]);
        Assert.Equal(["Later", "Earlier"], sessions.Select(session => session.Title));
    }

    [Fact]
    public async Task Book_page_points_at_the_next_volume()
    {
        var first = await CreateAsync(new CreateBookRequest(
            "Rocannon's World", "Ursula K. Le Guin", BookStatus.Finished, 5, Series: "Ekumen", SeriesNumber: 1));
        var second = await CreateAsync(new CreateBookRequest(
            "Planet of Exile", "Ursula K. Le Guin", BookStatus.Want, null, Series: "Ekumen", SeriesNumber: 2));

        var page = await _client.GetAsync($"/library/{first.Id}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Next:", html);
        Assert.Contains("Planet of Exile", html);
        Assert.Contains($"/library/{second.Id}", html);

        var last = await _client.GetAsync($"/library/{second.Id}");
        var lastHtml = await last.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Next:", lastHtml);
    }

    [Fact]
    public void Next_volume_skips_a_different_series()
    {
        var current = new Book { Id = 1, Title = "A", Author = "A", Series = "Earthsea", SeriesNumber = 1 };
        var shelf = new[]
        {
            new Book { Id = 2, Title = "Other", Author = "A", Series = "Hainish", SeriesNumber = 2 },
            new Book { Id = 3, Title = "The Tombs of Atuan", Author = "A", Series = "earthsea", SeriesNumber = 2 },
            current,
        };
        Assert.Equal(3, BookRules.NextInSeries(current, shelf)?.Id);
        Assert.Null(BookRules.NextInSeries(new Book { Id = 9, Title = "Loose", Author = "A" }, shelf));
    }

    [Fact]
    public async Task Places_group_books_and_a_recommendation_is_kept()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "Always Coming Home",
            "Ursula K. Le Guin",
            BookStatus.Want,
            null,
            Location: "  North wall  ",
            RecommendedBy: "  Ged  "));
        Assert.Equal("North wall", book.Location);
        Assert.Equal("Ged", book.RecommendedBy);

        var places = await _client.GetFromJsonAsync<PlaceCount[]>("/books/places", JsonOptions);
        Assert.Contains(places!, place => place.Name == "North wall" && place.Count >= 1);

        var filtered = await _client.GetFromJsonAsync<BookResponse[]>("/books?place=north%20wall", JsonOptions);
        Assert.Contains(filtered!, item => item.Id == book.Id);

        var page = await _client.GetAsync("/places");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("North wall", html);
    }

    [Fact]
    public async Task Recommenders_group_the_people_who_suggested_books()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "Tales of Ogion",
            "Ursula K. Le Guin",
            BookStatus.Want,
            null,
            RecommendedBy: "  Ogion  "));
        Assert.Equal("Ogion", book.RecommendedBy);

        var people = await _client.GetFromJsonAsync<RecommenderCount[]>("/books/recommenders", JsonOptions);
        Assert.Contains(people!, person => person.Name == "Ogion" && person.Count >= 1);

        var filtered = await _client.GetFromJsonAsync<BookResponse[]>("/books?recommendedBy=ogion", JsonOptions);
        Assert.Contains(filtered!, item => item.Id == book.Id);

        var page = await _client.GetAsync("/recommenders");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Ogion", html);
        Assert.Contains("/?recommendedBy=Ogion", html);

        var shelf = await _client.GetAsync("/?recommendedBy=Ogion");
        var shelfHtml = await shelf.Content.ReadAsStringAsync();
        Assert.Contains("From <strong>Ogion</strong>", shelfHtml);
        Assert.Contains("Tales of Ogion", shelfHtml);
    }

    [Fact]
    public void Recommenders_collapse_spelling_and_skip_blanks()
    {
        var people = BookRules.Recommenders(["  Ogion  ", "ogion", "  ", null, "Ged"]);
        Assert.Equal(["Ged", "Ogion"], people.Select(person => person.Name));
        Assert.Equal(2, people.Single(person => person.Name == "Ogion").Count);
    }

    [Fact]
    public void A_reading_streak_stops_when_a_day_is_missed()
    {
        var today = new DateOnly(2026, 10, 9);
        Assert.Equal(3, BookRules.ReadingStreak(
            [today, today.AddDays(-1), today.AddDays(-2), today.AddDays(-2)],
            today));
        Assert.Equal(1, BookRules.ReadingStreak([today.AddDays(-1)], today));
        Assert.Equal(0, BookRules.ReadingStreak([today.AddDays(-2)], today));
        Assert.Equal(0, BookRules.ReadingStreak([], today));
    }

    [Fact]
    public async Task Quote_search_matches_the_words_and_the_book()
    {
        var book = await CreateAsync(new CreateBookRequest("The Word for World Is Forest", "Ursula K. Le Guin", BookStatus.Want, null));
        await _client.PostAsJsonAsync(
            $"/books/{book.Id}/quotes",
            new CreateQuoteRequest("The word for world is forest.", 12),
            JsonOptions);

        var byWords = await _client.GetFromJsonAsync<QuoteListItem[]>("/books/quotes?q=forest", JsonOptions);
        Assert.Contains(byWords!, quote => quote.BookId == book.Id);

        var byTitle = await _client.GetFromJsonAsync<QuoteListItem[]>("/books/quotes?q=word%20for%20world", JsonOptions);
        Assert.Contains(byTitle!, quote => quote.BookId == book.Id);

        var none = await _client.GetFromJsonAsync<QuoteListItem[]>("/books/quotes?q=zzzz-no-such-quote", JsonOptions);
        Assert.DoesNotContain(none!, quote => quote.BookId == book.Id);
    }

    [Fact]
    public void Reading_days_are_the_distinct_days_in_that_month()
    {
        var days = BookRules.ReadingDays(
            [
                new DateOnly(2026, 10, 9),
                new DateOnly(2026, 10, 9),
                new DateOnly(2026, 10, 2),
                new DateOnly(2026, 9, 30),
            ],
            2026,
            10);
        Assert.Equal([2, 9], days);

        var book = new Book
        {
            Id = 4,
            Title = "The Eye of the Heron",
            Author = "Ursula K. Le Guin",
            Sessions = [new ReadingSession { Date = new DateOnly(2026, 10, 9), FromPage = 1, ToPage = 20 }],
        };
        var readings = BookRules.ReadingsOn([book], new DateOnly(2026, 10, 9));
        Assert.Equal((4, "The Eye of the Heron", 1, 20), (readings.Single().BookId, readings.Single().Title, readings.Single().FromPage, readings.Single().ToPage));
        Assert.Empty(BookRules.ReadingsOn([book], new DateOnly(2026, 10, 8)));
    }

    [Fact]
    public async Task Calendar_lists_the_days_a_book_was_read()
    {
        var book = await CreateAsync(new CreateBookRequest("The Eye of the Heron", "Ursula K. Le Guin", BookStatus.Reading, null));
        var created = await _client.PostAsJsonAsync(
            $"/books/{book.Id}/sessions",
            new CreateSessionRequest(new DateOnly(2026, 3, 14), 1, 20, null),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var month = await _client.GetFromJsonAsync<ReadingMonth>("/books/calendar?year=2026&month=3", JsonOptions);
        Assert.Equal(2026, month!.Year);
        Assert.Equal(3, month.Month);
        Assert.Contains(14, month.Days);

        var other = await _client.GetFromJsonAsync<ReadingMonth>("/books/calendar?year=2026&month=4", JsonOptions);
        Assert.DoesNotContain(14, other!.Days);

        var bad = await _client.GetAsync("/books/calendar?year=2026&month=13");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var page = await _client.GetAsync("/stats");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Earlier", html);
        Assert.Contains(DateOnly.FromDateTime(DateTime.UtcNow).ToString("MMMM yyyy"), html);
    }

    [Fact]
    public void Finished_books_group_by_the_year_they_were_finished()
    {
        var books = new[]
        {
            new Book { Id = 1, Title = "B", Author = "A", Status = BookStatus.Finished, FinishedOn = new DateOnly(2019, 1, 2) },
            new Book { Id = 2, Title = "A", Author = "A", Status = BookStatus.Finished, FinishedOn = new DateOnly(2019, 6, 1) },
            new Book { Id = 3, Title = "C", Author = "A", Status = BookStatus.Want },
            new Book { Id = 4, Title = "D", Author = "A", Status = BookStatus.Finished },
        };

        var years = BookRules.FinishedByYear(books);
        Assert.Equal([2019, null], years.Select(year => year.Year));
        Assert.Equal(["A", "B"], years[0].Books.Select(book => book.Title));
        Assert.Equal("D", years[1].Books.Single().Title);
    }

    [Fact]
    public async Task Years_list_finished_books_by_the_year()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "Searoad",
            "Ursula K. Le Guin",
            BookStatus.Finished,
            4,
            FinishedOn: new DateOnly(2019, 6, 1)));
        Assert.Equal(new DateOnly(2019, 6, 1), book.FinishedOn);

        var years = await _client.GetFromJsonAsync<FinishedYear[]>("/books/years", JsonOptions);
        var year = years!.Single(item => item.Year == 2019);
        Assert.Contains(year.Books, item => item.Id == book.Id && item.FinishedOn == new DateOnly(2019, 6, 1));

        var page = await _client.GetAsync("/years");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("2019", html);
        Assert.Contains("Searoad", html);
    }

    [Fact]
    public async Task Loans_group_the_people_who_have_books()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "Four Ways to Forgiveness",
            "Ursula K. Le Guin",
            BookStatus.Reading,
            null,
            LoanedTo: "  Tenar  "));
        Assert.Equal("Tenar", book.LoanedTo);

        var people = await _client.GetFromJsonAsync<LoanCount[]>("/books/loans", JsonOptions);
        Assert.Contains(people!, person => person.Name == "Tenar" && person.Count >= 1);

        var filtered = await _client.GetFromJsonAsync<BookResponse[]>("/books?loanedTo=tenar", JsonOptions);
        Assert.Contains(filtered!, item => item.Id == book.Id);

        var page = await _client.GetAsync("/loans");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Tenar", html);
        Assert.Contains("/?loanedTo=Tenar", html);

        var shelf = await _client.GetAsync("/?loanedTo=Tenar");
        var shelfHtml = await shelf.Content.ReadAsStringAsync();
        Assert.Contains("Loaned to <strong>Tenar</strong>", shelfHtml);
        Assert.Contains("Four Ways to Forgiveness", shelfHtml);
    }

    [Fact]
    public void Loans_collapse_spelling_and_skip_blanks()
    {
        var people = BookRules.Loans(["  Tenar  ", "tenar", "  ", null, "Ged"]);
        Assert.Equal(["Ged", "Tenar"], people.Select(person => person.Name));
        Assert.Equal(2, people.Single(person => person.Name == "Tenar").Count);
    }

    [Fact]
    public void An_overdue_loan_is_counted_for_that_person()
    {
        var today = new DateOnly(2026, 10, 9);
        var people = BookRules.Loans(
            [
                ("Tenar", new DateOnly(2026, 10, 1)),
                ("Tenar", new DateOnly(2026, 10, 20)),
                ("Ged", null),
            ],
            today);

        Assert.Equal(1, people.Single(person => person.Name == "Tenar").Overdue);
        Assert.Equal(2, people.Single(person => person.Name == "Tenar").Count);
        Assert.Equal(0, people.Single(person => person.Name == "Ged").Overdue);
    }

    [Fact]
    public async Task A_loan_due_date_shows_on_the_loans_page()
    {
        var due = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        var book = await CreateAsync(new CreateBookRequest(
            "Changing Planes",
            "Ursula K. Le Guin",
            BookStatus.Reading,
            null,
            LoanedTo: "Shevek",
            DueOn: due));
        Assert.Equal(due, book.DueOn);

        var people = await _client.GetFromJsonAsync<LoanCount[]>("/books/loans", JsonOptions);
        Assert.Contains(people!, person => person.Name == "Shevek" && person.Overdue >= 1);

        var page = await _client.GetAsync($"/library/{book.Id}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("overdue", html);
        Assert.Contains(due.ToString("MMM d, yyyy"), html);
    }

    [Fact]
    public async Task A_queued_want_is_shown_as_the_one_to_read_next()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "Always Coming Home",
            "Ursula K. Le Guin",
            BookStatus.Want,
            null,
            Queued: true));
        Assert.True(book.Queued);

        var page = await _client.GetAsync("/");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Read next", html);
        Assert.Contains("Always Coming Home", html);

        var library = await _client.GetAsync($"/library/{book.Id}");
        var libraryHtml = await library.Content.ReadAsStringAsync();
        Assert.Contains("On the read-next list.", libraryHtml);

        var updated = await _client.PutAsJsonAsync(
            $"/books/{book.Id}",
            new UpdateBookRequest(
                "Always Coming Home",
                "Ursula K. Le Guin",
                BookStatus.Reading,
                null,
                Queued: true));
        var body = await updated.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.False(body!.Queued);
    }

    [Fact]
    public async Task A_copy_remembers_how_it_arrived()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "Lavinia",
            "Ursula K. Le Guin",
            BookStatus.Want,
            null,
            Acquisition: Acquisition.Gift));
        Assert.Equal(Acquisition.Gift, book.Acquisition);

        var page = await _client.GetAsync($"/library/{book.Id}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("A gift", html);

        // How a copy arrived shows in the list view, beside the rest of its details.
        var shelf = await _client.GetAsync("/?view=list");
        var shelfHtml = await shelf.Content.ReadAsStringAsync();
        Assert.Contains("A gift", shelfHtml);
        Assert.Contains("Lavinia", shelfHtml);
    }

    [Fact]
    public async Task Copies_group_books_by_their_condition()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "Lavinia",
            "Ursula K. Le Guin",
            BookStatus.Want,
            null,
            Acquisition: Acquisition.Gift,
            Condition: CopyCondition.Good));
        Assert.Equal(CopyCondition.Good, book.Condition);

        var groups = await _client.GetFromJsonAsync<ConditionGroup[]>("/books/copies", JsonOptions);
        var good = groups!.Single(group => group.Condition == CopyCondition.Good);
        Assert.Contains(good.Books, item => item.Id == book.Id && item.Acquisition == Acquisition.Gift);

        var page = await _client.GetAsync("/copies");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Good copy", html);
        Assert.Contains("Lavinia", html);
    }

    [Fact]
    public async Task An_uploaded_epub_can_be_read_by_chapter()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "The Word for World Is Forest",
            "Ursula K. Le Guin",
            BookStatus.Want,
            null));

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(SampleEpub("The word for world is forest."));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/epub+zip");
        content.Add(file, "file", "forest.epub");
        var uploaded = await _client.PostAsync($"/books/{book.Id}/ebook", content);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var html = await uploaded.Content.ReadAsStringAsync();
        Assert.Contains("Read this e-book", html);
        Assert.Contains("forest.epub", html);

        var chapter = await _client.GetAsync($"/books/{book.Id}/ebook/chapters/0");
        Assert.Equal(HttpStatusCode.OK, chapter.StatusCode);
        Assert.Contains("The word for world is forest.", await chapter.Content.ReadAsStringAsync());

        var reader = await _client.GetAsync($"/library/{book.Id}/read");
        var readerHtml = await reader.Content.ReadAsStringAsync();
        Assert.Contains("Previous", readerHtml);
        Assert.Contains("Chapter One", readerHtml);

        var removed = await _client.DeleteAsync($"/books/{book.Id}/ebook");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var gone = await _client.GetAsync($"/books/{book.Id}/ebook/chapters/0");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task A_file_that_is_not_an_epub_or_pdf_is_refused()
    {
        var book = await CreateAsync(new CreateBookRequest("Notes", "Someone", BookStatus.Want, null));
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("hello"u8.ToArray()), "file", "notes.txt");
        var uploaded = await _client.PostAsync($"/books/{book.Id}/ebook", content);
        var html = await uploaded.Content.ReadAsStringAsync();
        Assert.Contains("Choose an EPUB, a PDF, a comic (CBZ), or a Kindle file.", html);
        var stored = await _client.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions);
        Assert.Null(stored?.EbookFileName);
    }

    [Fact]
    public void An_epub_spine_becomes_chapters()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shelf-epub-{Guid.NewGuid():N}.epub");
        try
        {
            File.WriteAllBytes(path, SampleEpub("Opened from the spine."));
            var chapters = EpubFile.Chapters(path);
            Assert.NotNull(chapters);
            Assert.Equal("Chapter One", chapters[0].Title);
            var html = EpubFile.ChapterHtml(path, 0, "/books/1/ebook/assets");
            Assert.Contains("Opened from the spine.", html);
            Assert.Contains("""<base href="/books/1/ebook/assets/OEBPS/" />""", html);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task A_highlight_is_kept_with_its_note_and_painted_in_the_chapter()
    {
        var book = await CreateAsync(new CreateBookRequest("Always Coming Home", "Ursula K. Le Guin", BookStatus.Want, null));
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(SampleEpub("The word for world is forest.")), "file", "home.epub");
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/books/{book.Id}/ebook", content)).StatusCode);

        var created = await _client.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest(
            "The word for world is forest.",
            0,
            "See <the> forest.",
            "not the real prefix",
            null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var highlight = await created.Content.ReadFromJsonAsync<HighlightResponse>(JsonOptions);
        Assert.NotNull(highlight);
        Assert.Equal("See <the> forest.", highlight.Note);

        var chapter = await _client.GetStringAsync($"/books/{book.Id}/ebook/chapters/0");
        Assert.Contains("shelf-marks", chapter);
        Assert.Contains("The word for world is forest.", chapter);
        Assert.Contains("""See \u003Cthe\u003E forest.""", chapter);
        Assert.DoesNotContain("<the>", chapter);

        var page = await _client.GetStringAsync($"/library/{book.Id}");
        Assert.Contains("The word for world is forest.", page);
        Assert.Contains("See &lt;the&gt; forest.", page);

        var updated = await _client.PutAsJsonAsync(
            $"/books/{book.Id}/highlights/{highlight.Id}",
            new UpdateHighlightRequest("A quieter note."));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var list = await _client.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions);
        Assert.Equal("A quieter note.", Assert.Single(list!).Note);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/books/{book.Id}/highlights/{highlight.Id}")).StatusCode);
        var after = await _client.GetStringAsync($"/books/{book.Id}/ebook/chapters/0");
        Assert.DoesNotContain("A quieter note.", after);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/books/{book.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_blank_highlight_is_refused()
    {
        var book = await CreateAsync(new CreateBookRequest("The Dispossessed", "Ursula K. Le Guin", BookStatus.Want, null));
        var created = await _client.PostAsJsonAsync(
            $"/books/{book.Id}/highlights",
            new CreateHighlightRequest("  ", 0));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    [Fact]
    public async Task An_uploaded_recording_can_be_played()
    {
        var book = await CreateAsync(new CreateBookRequest("The Farthest Shore", "Ursula K. Le Guin", BookStatus.Want, null));
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("a quiet shore"u8.ToArray()), "file", "shore.mp3");
        var uploaded = await _client.PostAsync($"/books/{book.Id}/audio", content);
        var html = await uploaded.Content.ReadAsStringAsync();
        Assert.Contains("The audiobook is on the shelf.", html);
        Assert.Contains("Listen to this audiobook", html);
        Assert.Contains("shore.mp3", html);

        var stored = await _client.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions);
        Assert.Equal("shore.mp3", stored?.AudioFileName);
        Assert.Equal(BookFormat.Audiobook, stored?.Format);

        var track = await _client.GetAsync($"/books/{book.Id}/audio/tracks/0");
        Assert.Equal(HttpStatusCode.OK, track.StatusCode);
        Assert.Equal("audio/mpeg", track.Content.Headers.ContentType?.MediaType);
        Assert.Equal("a quiet shore", await track.Content.ReadAsStringAsync());

        var player = await _client.GetStringAsync($"/library/{book.Id}/listen");
        Assert.Contains($"/books/{book.Id}/audio/tracks/0", player);
        Assert.Contains("shore.mp3", player);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/books/{book.Id}/audio")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/books/{book.Id}/audio/tracks/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/books/{book.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_zip_of_tracks_plays_in_order()
    {
        var book = await CreateAsync(new CreateBookRequest("Tehanu", "Ursula K. Le Guin", BookStatus.Want, null));
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "part/02-b.mp3", "second");
            WriteEntry(zip, "part/01-a.mp3", "first");
        }

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(memory.ToArray()), "file", "tehanu.zip");
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/books/{book.Id}/audio", content)).StatusCode);

        Assert.Equal("first", await _client.GetStringAsync($"/books/{book.Id}/audio/tracks/0"));
        Assert.Equal("second", await _client.GetStringAsync($"/books/{book.Id}/audio/tracks/1"));
        var player = await _client.GetStringAsync($"/library/{book.Id}/listen");
        Assert.Contains("01-a.mp3", player);
        Assert.Contains("02-b.mp3", player);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/books/{book.Id}/audio/tracks/2")).StatusCode);
    }

    [Fact]
    public async Task A_file_that_is_not_audio_is_refused()
    {
        var book = await CreateAsync(new CreateBookRequest("The Other Wind", "Ursula K. Le Guin", BookStatus.Want, null));
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("hello"u8.ToArray()), "file", "notes.txt");
        var uploaded = await _client.PostAsync($"/books/{book.Id}/audio", content);
        var html = await uploaded.Content.ReadAsStringAsync();
        Assert.Contains("Choose an audio file, or a zip of them.", html);
        var stored = await _client.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions);
        Assert.Null(stored?.AudioFileName);
    }

    [Fact]
    public async Task Devices_trade_an_e_book_and_keep_the_further_place()
    {
        var book = await CreateAsync(new CreateBookRequest("Unlocking the Air", "Ursula K. Le Guin", BookStatus.Want, null));

        // Built once: a zip carries the time it was made, so two builds can differ by a second.
        var epub = SampleEpub("A heron waits.");
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent(epub), "file", "heron.epub");
            Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/books/{book.Id}/ebook", content)).StatusCode);
        }

        var catalog = await _client.GetFromJsonAsync<SyncCatalog>("/books/sync", JsonOptions);
        var entry = catalog!.Books.Single(item => item.Title == "Unlocking the Air");
        Assert.Equal("heron.epub", entry.Ebook?.FileName);
        Assert.Equal(ShelfSync.Key(null, book.Title, book.Author), entry.Key);

        var file = await _client.GetByteArrayAsync($"/books/sync/{entry.Key}/ebook");
        Assert.Equal(epub, file);

        var ahead = await _client.PutAsJsonAsync($"/books/sync/{entry.Key}/progress", new SyncProgress(
            entry.Ebook!.Sha256, 3, null, null, null,
            [new SyncHighlight(0, "A heron waits.", "At the start.", null, null)]));
        Assert.Equal(HttpStatusCode.NoContent, ahead.StatusCode);
        var moved = await _client.GetFromJsonAsync<SyncCatalog>("/books/sync", JsonOptions);
        Assert.Equal(3, moved!.Books.Single(item => item.Key == entry.Key).EbookChapter);
        var marks = await _client.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions);
        Assert.Equal("At the start.", Assert.Single(marks!).Note);

        var back = await _client.PutAsJsonAsync($"/books/sync/{entry.Key}/progress", new SyncProgress(
            entry.Ebook.Sha256, 1, null, null, null,
            [new SyncHighlight(0, "A heron waits.", "A second note.", null, null)]));
        Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        var stayed = await _client.GetFromJsonAsync<SyncCatalog>("/books/sync", JsonOptions);
        Assert.Equal(3, stayed!.Books.Single(item => item.Key == entry.Key).EbookChapter);
        marks = await _client.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions);
        Assert.Equal("At the start.", Assert.Single(marks!).Note);

        var ignored = await _client.PutAsJsonAsync($"/books/sync/{entry.Key}/progress", new SyncProgress(
            "not-the-file", 9, null, null, null,
            [new SyncHighlight(0, "Somewhere else.", null, null, null)]));
        Assert.Equal(HttpStatusCode.NoContent, ignored.StatusCode);
        marks = await _client.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions);
        Assert.Single(marks!);

        using var other = new MultipartFormDataContent();
        other.Add(new ByteArrayContent(SampleEpub("A different text.")), "file", "other.epub");
        var conflict = await _client.PostAsync($"/books/sync/{entry.Key}/ebook", other);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("A heron waits.", await _client.GetStringAsync($"/books/{book.Id}/ebook/chapters/0"));

        var page = await _client.GetStringAsync("/sync");
        Assert.Contains("Address of the other shelf", page);
    }

    [Fact]
    public async Task A_shelf_brings_an_audiobook_it_does_not_have()
    {
        var book = await CreateAsync(new CreateBookRequest("The Compass Rose", "Ursula K. Le Guin", BookStatus.Want, null));
        var catalog = new SyncCatalog([
            new SyncBook(
                ShelfSync.Key(null, book.Title, book.Author),
                book.Title,
                book.Author,
                null,
                null,
                1,
                9,
                null,
                new SyncFile("shore.mp3", 12, "unused"),
                []),
        ]);

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Shelf.Api.Readers.ShelfReader>().Use(factory.ReaderId);
        var lines = await ShelfSync.ExchangeAsync(
            scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>(),
            scope.ServiceProvider.GetRequiredService<EbookStore>(),
            scope.ServiceProvider.GetRequiredService<AudioStore>(),
            catalog,
            (_, kind, _) => Task.FromResult<Stream?>(kind == "audio" ? ZipText("shore.mp3", "a quiet shore") : null),
            (_, _) => Task.FromResult<string?>(null),
            (_, _, _, _, _) => Task.FromResult(204),
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Contains("Brought the audiobook of The Compass Rose.", lines);
        Assert.Equal("a quiet shore", await _client.GetStringAsync($"/books/{book.Id}/audio/tracks/0"));
        var synced = await _client.GetFromJsonAsync<SyncCatalog>("/books/sync", JsonOptions);
        var entry = synced!.Books.Single(item => item.Title == book.Title);
        Assert.Equal(1, entry.AudioTrack);
        Assert.Equal(9, entry.AudioSeconds);

        var zip = await _client.GetByteArrayAsync($"/books/sync/{entry.Key}/audio");
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        var packed = archive.Entries.Single();
        using var reader = new StreamReader(packed.Open());
        Assert.Equal("a quiet shore", reader.ReadToEnd());
    }

    [Fact]
    public async Task Asking_for_a_book_twice_keeps_one_copy()
    {
        var first = await _client.PostAsJsonAsync("/books/sync/books", new SyncOffer("Searoad Stories", "Ursula K. Le Guin", null), JsonOptions);
        var second = await _client.PostAsJsonAsync("/books/sync/books", new SyncOffer("Searoad Stories", "Ursula K. Le Guin", null), JsonOptions);
        var left = await first.Content.ReadFromJsonAsync<SyncPlace>(JsonOptions);
        var right = await second.Content.ReadFromJsonAsync<SyncPlace>(JsonOptions);
        Assert.Equal(left?.Key, right?.Key);
        var found = await _client.GetFromJsonAsync<BookResponse[]>("/books?q=Searoad%20Stories", JsonOptions);
        Assert.Single(found!, item => item.Title == "Searoad Stories");
    }

    internal static Stream ZipText(string name, string text)
    {
        var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, name, text);
        }

        memory.Position = 0;
        return memory;
    }

    private static void WriteEntry(System.IO.Compression.ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    internal static byte[] SampleEpub(string sentence)
    {
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "META-INF/container.xml", """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                  </rootfiles>
                </container>
                """);
            Write(zip, "OEBPS/content.opf", """
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>Sample</dc:title>
                  </metadata>
                  <manifest>
                    <item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
                  </manifest>
                  <spine>
                    <itemref idref="c1"/>
                  </spine>
                </package>
                """);
            Write(zip, "OEBPS/chapter1.xhtml", $"""
                <?xml version="1.0"?>
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <head><title>Chapter One</title></head>
                  <body><p>{sentence}</p></body>
                </html>
                """);
        }

        return memory.ToArray();

        static void Write(System.IO.Compression.ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(text);
        }
    }

    [Fact]
    public async Task An_original_title_is_kept_and_can_be_searched()
    {
        var book = await CreateAsync(new CreateBookRequest(
            "The Telling",
            "Ursula K. Le Guin",
            BookStatus.Want,
            null,
            OriginalTitle: "  Aka  "));
        Assert.Equal("Aka", book.OriginalTitle);

        var found = await _client.GetFromJsonAsync<BookResponse[]>("/books?q=aka", JsonOptions);
        Assert.Contains(found!, item => item.Id == book.Id);

        var page = await _client.GetAsync($"/library/{book.Id}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Originally", html);
        Assert.Contains("Aka", html);
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

public sealed partial class ShelfApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "a long enough password";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"shelf-{Guid.NewGuid():N}.db");
    private readonly string _ebookRoot = Path.Combine(Path.GetTempPath(), $"shelf-ebooks-{Guid.NewGuid():N}");
    private readonly string _audioRoot = Path.Combine(Path.GetTempPath(), $"shelf-audio-{Guid.NewGuid():N}");
    private readonly string _coverRoot = Path.Combine(Path.GetTempPath(), $"shelf-covers-{Guid.NewGuid():N}");
    private readonly string _keysRoot = Path.Combine(Path.GetTempPath(), $"shelf-keys-{Guid.NewGuid():N}");
    private HttpClient? _client;

    // The reader most tests act as: the first account on this shelf.
    public HttpClient Client => _client ?? throw new InvalidOperationException("The factory has not signed in yet.");

    public int ReaderId { get; private set; }

    // Every email the shelf sends in these tests, instead of a mail server.
    public CapturedMail Mail { get; } = new();

    public async Task InitializeAsync()
    {
        _client = await SignUpAsync("Tenar");
        ReaderId = await ReaderIdAsync("Tenar");
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    public async Task<HttpClient> SignUpAsync(string name, string password = Password)
    {
        var client = CreateClient();
        var response = await PostFormAsync(client, "/signup", "/account/signup", new()
        {
            ["name"] = name,
            ["password"] = password,
            ["confirm"] = password,
        });
        Assert.Equal("/", response.RequestMessage?.RequestUri?.AbsolutePath);
        return client;
    }

    public async Task<int> ReaderIdAsync(string name)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
        var normalized = Shelf.Api.Readers.ReaderRules.Normalize(name);
        return db.Readers.Single(reader => reader.NormalizedName == normalized).Id;
    }

    // Sends a form the way the browser does: read the page for its antiforgery token, then post.
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string page,
        string action,
        Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(page);
        var token = AntiforgeryToken().Match(html);
        Assert.True(token.Success, $"No antiforgery token on {page}.");
        fields["__RequestVerificationToken"] = System.Net.WebUtility.HtmlDecode(token.Groups[1].Value);
        return await client.PostAsync(action, new FormUrlEncodedContent(fields));
    }

    [System.Text.RegularExpressions.GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial System.Text.RegularExpressions.Regex AntiforgeryToken();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Shelf", $"Data Source={_databasePath}");
        builder.UseSetting("EbookStore:Root", _ebookRoot);
        builder.UseSetting("AudioStore:Root", _audioRoot);
        builder.UseSetting("CoverStore:Root", _coverRoot);
        builder.UseSetting("Accounts:SignInsPerMinute", "1000");
        builder.UseSetting("DataProtection:KeysPath", _keysRoot);
        builder.UseSetting("Backup:Enabled", "false");
        builder.UseSetting("Backup:Folder", Path.Combine(_keysRoot, "backups"));
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IBookLookup, StubBookLookup>();
            services.AddSingleton<Shelf.Api.Readers.IEmailSender>(Mail);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await base.DisposeAsync();
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        if (Directory.Exists(_ebookRoot))
        {
            Directory.Delete(_ebookRoot, recursive: true);
        }

        foreach (var folder in new[] { _audioRoot, _coverRoot, _keysRoot })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
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
    public Task<CatalogMatch?> FindAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken)
    {
        if (title?.Contains("Earthsea", StringComparison.OrdinalIgnoreCase) == true
            && author?.Contains("Le Guin", StringComparison.OrdinalIgnoreCase) == true
            && BookRules.NormalizeIsbn(isbn) is null)
        {
            return Task.FromResult<CatalogMatch?>(new CatalogMatch(
                "A Wizard of Earthsea",
                "Ursula K. Le Guin",
                1968,
                205,
                "Parnassus Press",
                "English",
                "https://covers.openlibrary.org/b/id/2-M.jpg",
                null,
                null,
                BookFormat.Hardcover,
                ["Fantasy"]));
        }

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

public sealed class CapturedMail : Shelf.Api.Readers.IEmailSender
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Shelf.Api.Readers.EmailMessage> _sent = new();

    public bool Enabled => true;

    public IReadOnlyList<Shelf.Api.Readers.EmailMessage> Sent => [.. _sent];

    public Task SendAsync(Shelf.Api.Readers.EmailMessage message, CancellationToken cancellationToken)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
