using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class ClubTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_club_reads_together_and_talks_about_each_book()
    {
        var host = await factory.SignUpAsync("Club Host");
        var guest = await factory.SignUpAsync("Club Guest");
        var outsider = await factory.SignUpAsync("Club Outsider");
        var guestId = await factory.ReaderIdAsync("Club Guest");
        var own = (await (await host.PostAsJsonAsync("/books", new CreateBookRequest("Kindred", "Octavia E. Butler", BookStatus.Reading, null, Isbn: "9780807083697"), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;

        var club = (await (await host.PostAsJsonAsync("/books/clubs", new ClubRequest("Thursday readers", "Every other Thursday."), JsonOptions)).Content.ReadFromJsonAsync<ClubResponse>(JsonOptions))!;
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/books/clubs/{club.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.PostAsJsonAsync($"/books/clubs/{club.Id}/members", new ClubInvite(guestId), JsonOptions)).StatusCode);
        Assert.Equal(["Thursday readers"], (await guest.GetFromJsonAsync<ClubSummary[]>("/books/clubs", JsonOptions))!.Select(item => item.Name));
        Assert.Empty((await outsider.GetFromJsonAsync<ClubSummary[]>("/books/clubs", JsonOptions))!);

        // The host adds a book from their shelf; it becomes the one being read. The guest adds another by name.
        Assert.Equal(HttpStatusCode.NoContent, (await host.PostAsJsonAsync($"/books/clubs/{club.Id}/books", new ClubBookRequest(BookId: own.Id), JsonOptions)).StatusCode);
        await guest.PostAsJsonAsync($"/books/clubs/{club.Id}/books", new ClubBookRequest(Title: "Parable of the Sower", Author: "Octavia E. Butler"), JsonOptions);
        var seen = (await guest.GetFromJsonAsync<ClubResponse>($"/books/clubs/{club.Id}", JsonOptions))!;
        var kindred = seen.Books.Single(book => book.Title == "Kindred");
        Assert.Equal(kindred.Id, seen.CurrentBookId);
        Assert.Null(kindred.OnMyShelf);
        Assert.Equal(own.Id, (await host.GetFromJsonAsync<ClubResponse>($"/books/clubs/{club.Id}", JsonOptions))!.Books.Single(book => book.Title == "Kindred").OnMyShelf);

        // Talking about it, and taking it onto the guest's own shelf, once.
        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostAsJsonAsync($"/books/clubs/{club.Id}/books/{kindred.Id}/posts", new ClubPostRequest("Dana's 1976 is so vivid."), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/books/clubs/{club.Id}/books/{kindred.Id}/posts", new ClubPostRequest("Let me in."), JsonOptions)).StatusCode);
        var posts = (await host.GetFromJsonAsync<ClubPostResponse[]>($"/books/clubs/{club.Id}/books/{kindred.Id}/posts", JsonOptions))!;
        Assert.Equal(("Club Guest", "Dana's 1976 is so vivid.", false), (Assert.Single(posts).Reader, posts[0].Text, posts[0].Mine));
        var shelved = await (await guest.PostAsync($"/books/clubs/{club.Id}/books/{kindred.Id}/shelve", null)).Content.ReadFromJsonAsync<JsonElement>();
        var again = await (await guest.PostAsync($"/books/clubs/{club.Id}/books/{kindred.Id}/shelve", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(shelved.GetProperty("id").GetInt32(), again.GetProperty("id").GetInt32());
        var copy = (await guest.GetFromJsonAsync<BookResponse>($"/books/{shelved.GetProperty("id").GetInt32()}", JsonOptions))!;
        Assert.Equal(("Kindred", BookStatus.Want, "9780807083697"), (copy.Title, copy.Status, copy.Isbn));

        // Only the host removes others or ends the club; the guest can leave.
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.DeleteAsync($"/books/clubs/{club.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await guest.DeleteAsync($"/books/clubs/{club.Id}/members/{guestId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/books/clubs/{club.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/books/clubs/{club.Id}")).StatusCode);
        Assert.Empty((await host.GetFromJsonAsync<ClubSummary[]>("/books/clubs", JsonOptions))!);

        Assert.Contains("Book clubs", await host.GetStringAsync("/clubs"));
    }
}
