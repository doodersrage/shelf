using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
