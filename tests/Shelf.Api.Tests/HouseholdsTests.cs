using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class HouseholdsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static async Task<BookResponse> BookAsync(HttpClient client, string title, string[]? tags = null)
    {
        var book = (await (await client.PostAsJsonAsync("/books", new CreateBookRequest(title, "Someone", BookStatus.Reading, 5, Notes: "The owner's private note.", Tags: tags), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using var content = new MultipartFormDataContent { { new ByteArrayContent(BooksEndpointTests.SampleEpub($"The text of {title}.")), "file", "book.epub" } };
        await client.PostAsync($"/books/{book.Id}/ebook", content);
        return book;
    }

    // A collection shared as a household library: a member reads its books with a place and highlights of their own,
    // never the owner's, nor the owner's notes; a reader it isn't shared with gets nothing; a member who can't
    // download can still read, but not take the file; one with explicit books kept from them doesn't see those; and
    // let go, the books close to them.
    [Fact]
    public async Task A_household_library_opens_its_books_to_its_members_alone()
    {
        var owner = await factory.SignUpAsync("Household Owner");
        var member = await factory.SignUpAsync("Household Member");
        var outsider = await factory.SignUpAsync("Household Outsider");
        var memberId = await factory.ReaderIdAsync("Household Member");
        var book = await BookAsync(owner, "Read at Home");
        var adult = await BookAsync(owner, "Not for Everyone", ["explicit"]);
        var kept = await BookAsync(owner, "Kept Back");
        var collection = (await (await owner.PostAsJsonAsync("/books/collections", new CollectionRequest("Family Room"), JsonOptions)).Content.ReadFromJsonAsync<CollectionResponse>(JsonOptions))!;
        foreach (var each in new[] { book, adult })
        {
            await owner.PostAsJsonAsync($"/books/collections/{collection.Id}/books", new CollectionBookRequest(each.Id), JsonOptions);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/books/{book.Id}/ebook/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/books/collections/{collection.Id}/members", new MemberRequest(memberId), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/books/collections/{collection.Id}/members", new MemberRequest(memberId), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/books/collections/{collection.Id}/members", new MemberRequest(await factory.ReaderIdAsync("Household Owner")), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/books/collections/{collection.Id}/members", new MemberRequest(await factory.ReaderIdAsync("Household Outsider")), JsonOptions)).StatusCode);

        var library = Assert.Single((await member.GetFromJsonAsync<HouseholdLibrary[]>("/books/households", JsonOptions))!);
        Assert.Equal(("Family Room", "Household Owner"), (library.Name, library.Owner));
        Assert.Equal(["Read at Home", "Not for Everyone"], library.Books.Select(item => item.Title));
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/books/{book.Id}/ebook/file")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/books/{book.Id}/ebook/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/books/{kept.Id}/ebook/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/books/{book.Id}")).StatusCode);
        Assert.DoesNotContain("private note", await member.GetStringAsync("/books/households"));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/books/{book.Id}/ebook/file")).StatusCode);
        Assert.Empty((await outsider.GetFromJsonAsync<HouseholdLibrary[]>("/books/households", JsonOptions))!);

        // Places and highlights of their own, each unseen by the other.
        await owner.PutAsJsonAsync($"/books/{book.Id}/place", new PlaceRequest(0), JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, (await member.PutAsJsonAsync($"/books/{book.Id}/place", new PlaceRequest(3), JsonOptions)).StatusCode);
        Assert.Equal(3, (await member.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);
        Assert.Equal(0, (await owner.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);
        await member.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("The text", 0, "The member's thought."), JsonOptions);
        await owner.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("The text", 0, "The owner's thought."), JsonOptions);
        Assert.Equal(["The member's thought."], (await member.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions))!.Select(mark => mark.Note));
        Assert.Equal(["The owner's thought."], (await owner.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions))!.Select(mark => mark.Note));

        // No downloads, and explicit books kept back: read still, but not taken, and the explicit one gone from view.
        var membership = Assert.Single((await owner.GetFromJsonAsync<HouseholdMember[]>($"/books/collections/{collection.Id}/members", JsonOptions))!);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/books/collections/{collection.Id}/members/{membership.Id}", new MemberChange(CanDownload: false, HideExplicit: true), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/books/{book.Id}/ebook/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/books/{book.Id}/ebook/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/books/{adult.Id}/ebook/file")).StatusCode);
        Assert.Equal(["Read at Home"], Assert.Single((await member.GetFromJsonAsync<HouseholdLibrary[]>("/books/households", JsonOptions))!).Books.Select(item => item.Title));

        // Let go: closed at once.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/books/collections/{collection.Id}/members/{membership.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/books/{book.Id}/ebook/file")).StatusCode);
        Assert.Empty((await member.GetFromJsonAsync<HouseholdLibrary[]>("/books/households", JsonOptions))!);
    }

    // A child's account reads what its guardian shares with their children, and can't be made a household member.
    [Fact]
    public async Task A_childs_account_cant_join_a_household_library()
    {
        var owner = await factory.SignUpAsync("Household Parent");
        var ownerId = await factory.ReaderIdAsync("Household Parent");
        int childId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(ownerId);
            childId = (await Family.CreateChildAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), "Household Kid", ShelfApiFactory.Password)).Child!.Id;
        }

        var collection = (await (await owner.PostAsJsonAsync("/books/collections", new CollectionRequest("Grown-ups"), JsonOptions)).Content.ReadFromJsonAsync<CollectionResponse>(JsonOptions))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/books/collections/{collection.Id}/members", new MemberRequest(childId), JsonOptions)).StatusCode);
    }
}
