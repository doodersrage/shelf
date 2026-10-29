using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class NarratorsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // Each voice with the books they read, a book read by two counted for each, and the library found by one of them.
    [Fact]
    public async Task Narrators_are_listed_with_their_books_and_found_by_name()
    {
        var reader = await factory.SignUpAsync("Narrator Lister");
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Read Alone", "Someone", BookStatus.Want, null, Narrator: "Ann Voice"), JsonOptions);
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Read Together", "Someone", BookStatus.Want, null, Narrator: "Ann Voice & Bo Reader"), JsonOptions);
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Read Silently", "Someone", BookStatus.Want, null), JsonOptions);

        var narrators = (await reader.GetFromJsonAsync<AuthorCount[]>("/books/narrators", JsonOptions))!;
        Assert.Equal([new AuthorCount("Ann Voice", 2), new AuthorCount("Bo Reader", 1)], narrators);
        Assert.Equal(["Read Together"], (await reader.GetFromJsonAsync<BookResponse[]>("/books?narrator=bo%20reader", JsonOptions))!.Select(book => book.Title));
        Assert.Contains("Read Together", await reader.GetStringAsync("/library?narrator=Bo%20Reader"));
        Assert.DoesNotContain("Read Alone", await reader.GetStringAsync("/library?narrator=Bo%20Reader"));
        Assert.Contains("Bo Reader", await reader.GetStringAsync("/narrators"));
        Assert.Empty((await (await factory.SignUpAsync("Narrator Stranger")).GetFromJsonAsync<AuthorCount[]>("/books/narrators", JsonOptions))!);
    }
}
