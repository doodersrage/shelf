using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class CollectionTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_collection_holds_its_books_in_the_order_given()
    {
        var reader = await factory.SignUpAsync("Collecting Reader");
        var first = await CreateAsync(reader, "The Dispossessed");
        var second = await CreateAsync(reader, "The Lathe of Heaven");
        var third = await CreateAsync(reader, "Always Coming Home");

        var made = await reader.PostAsJsonAsync("/books/collections", new CollectionRequest("Book club", "One a month."), JsonOptions);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var id = (await made.Content.ReadFromJsonAsync<CollectionResponse>(JsonOptions))!.Id;
        foreach (var book in new[] { first, second, third, first })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync($"/books/collections/{id}/books", new CollectionBookRequest(book.Id), JsonOptions)).StatusCode);
        }

        Assert.Equal(["The Dispossessed", "The Lathe of Heaven", "Always Coming Home"], await TitlesAsync(reader, id));
        await reader.PutAsJsonAsync($"/books/collections/{id}/order", new CollectionOrderRequest([third.Id, first.Id]), JsonOptions);
        Assert.Equal(["Always Coming Home", "The Dispossessed", "The Lathe of Heaven"], await TitlesAsync(reader, id));
        await reader.DeleteAsync($"/books/collections/{id}/books/{first.Id}");
        Assert.Equal(["Always Coming Home", "The Lathe of Heaven"], await TitlesAsync(reader, id));

        var listed = Assert.Single((await reader.GetFromJsonAsync<CollectionSummary[]>("/books/collections", JsonOptions))!);
        Assert.Equal(("Book club", "One a month.", 2), (listed.Name, listed.Description, listed.Books));

        // Deleting a book takes it out; deleting the collection leaves the books.
        await reader.DeleteAsync($"/books/{second.Id}");
        Assert.Equal(["Always Coming Home"], await TitlesAsync(reader, id));
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/collections/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/books/{third.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/books/collections", new CollectionRequest(" "), JsonOptions)).StatusCode);
    }

    [Fact]
    public async Task Collections_are_their_readers_own_and_can_be_shared_by_link()
    {
        var owner = await factory.SignUpAsync("Sharing Collector");
        var other = await factory.SignUpAsync("Curious Collector");
        var book = await CreateAsync(owner, "The Telling");
        var theirs = await CreateAsync(other, "Not Theirs To Add");
        var id = (await (await owner.PostAsJsonAsync("/books/collections", new CollectionRequest("To lend"), JsonOptions)).Content.ReadFromJsonAsync<CollectionResponse>(JsonOptions))!.Id;
        await owner.PostAsJsonAsync($"/books/collections/{id}/books", new CollectionBookRequest(book.Id), JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/books/collections/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/books/collections/{id}/books", new CollectionBookRequest(theirs.Id), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/books/collections/{id}/books", new CollectionBookRequest(theirs.Id), JsonOptions)).StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<CollectionSummary[]>("/books/collections", JsonOptions))!);

        string token;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Sharing Collector"));
            token = (await Collections.ShareAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), id))!;
        }

        var anyone = factory.CreateClient();
        var page = await anyone.GetStringAsync($"/shared/{token}");
        Assert.Contains("To lend", page);
        Assert.Contains("The Telling", page);
        Assert.Equal(["The Telling"], (await anyone.GetFromJsonAsync<SharedList>($"/shared/{token}/list.json", JsonOptions))!.Books.Select(item => item.Title));

        Assert.Contains($"/shared/{token}", await owner.GetStringAsync($"/collections/{id}"));
        Assert.Contains("To lend", await owner.GetStringAsync("/collections"));
    }

    private static async Task<BookResponse> CreateAsync(HttpClient client, string title) =>
        (await (await client.PostAsJsonAsync("/books", new CreateBookRequest(title, "Ursula K. Le Guin", BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;

    private static async Task<string[]> TitlesAsync(HttpClient client, int id) =>
        (await client.GetFromJsonAsync<CollectionResponse>($"/books/collections/{id}", JsonOptions))!.Books.Select(book => book.Title).ToArray();
}
