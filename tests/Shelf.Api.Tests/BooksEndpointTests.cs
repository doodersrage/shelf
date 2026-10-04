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
        var created = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest("Kindred", "Octavia E. Butler", BookStatus.Reading, null),
            JsonOptions);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var book = await created.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.NotNull(book);
        Assert.Equal("Kindred", book.Title);
        Assert.Equal(BookStatus.Reading, book.Status);

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
    public async Task Get_missing_book_returns_not_found()
    {
        var response = await _client.GetAsync("/books/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_and_delete_round_trip()
    {
        var created = await _client.PostAsJsonAsync(
            "/books",
            new CreateBookRequest("Piranesi", "Susanna Clarke", BookStatus.Want, null),
            JsonOptions);
        var book = await created.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.NotNull(book);

        var updated = await _client.PutAsJsonAsync(
            $"/books/{book.Id}",
            new UpdateBookRequest("Piranesi", "Susanna Clarke", BookStatus.Finished, 5, 2020),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var afterUpdate = await updated.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.Equal(BookStatus.Finished, afterUpdate?.Status);
        Assert.Equal(5, afterUpdate?.Rating);
        Assert.Equal(2020, afterUpdate?.Year);

        var deleted = await _client.DeleteAsync($"/books/{book.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var missing = await _client.GetAsync($"/books/{book.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
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
