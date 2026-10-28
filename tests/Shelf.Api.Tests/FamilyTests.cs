using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class FamilyTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_child_reads_what_their_guardian_shares_and_can_do_less()
    {
        var parent = await factory.SignUpAsync("Family Parent");
        var neighbour = await factory.SignUpAsync("Family Neighbour");
        var parentId = await factory.ReaderIdAsync("Family Parent");
        var neighbourId = await factory.ReaderIdAsync("Family Neighbour");
        int childId;
        await using (var scope = Scope(parentId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var (child, problem) = await Family.CreateChildAsync(db, "Family Kid", ShelfApiFactory.Password);
            Assert.Null(problem);
            childId = child!.Id;
            Assert.Equal("Family Kid", Assert.Single(await Family.ChildrenAsync(db)).Name);
        }

        var kid = factory.CreateClient();
        var signedIn = await ShelfApiFactory.PostFormAsync(kid, "/signin", "/account/signin", new() { ["name"] = "Family Kid", ["password"] = ShelfApiFactory.Password });
        Assert.Equal("/", signedIn.RequestMessage?.RequestUri?.AbsolutePath);

        // A book in a collection the parent shares opens for the child, as lent; others do not.
        var shared = (await (await parent.PostAsJsonAsync("/books", new CreateBookRequest("The Hobbit", "J. R. R. Tolkien", BookStatus.Finished, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        var kept = (await (await parent.PostAsJsonAsync("/books", new CreateBookRequest("Grown-up Book", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        Assert.Equal(HttpStatusCode.NotFound, (await kid.GetAsync($"/books/{shared.Id}/place")).StatusCode);
        await using (var scope = Scope(parentId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var (collection, _) = await Collections.CreateAsync(db, "For the kids", null);
            await Collections.AddAsync(db, collection!.Id, shared.Id);
            await Family.SetSharedAsync(db, collection.Id, true);
        }

        Assert.Equal(HttpStatusCode.OK, (await kid.GetAsync($"/books/{shared.Id}/place")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await kid.GetAsync($"/books/{kept.Id}/place")).StatusCode);
        await using (var scope = Scope(childId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var family = await Family.SharedBooksAsync(db);
            Assert.Equal(("Family Parent", "The Hobbit"), (family!.Value.Guardian, Assert.Single(family.Value.Books).Title));

            // Grown-up things are refused.
            Assert.NotNull((await ApiTokens.CreateAsync(db, childId, "a script", false)).Problem);
            await Asking.SetShelfOpenAsync(db, true);
            Assert.False(await Asking.IsShelfOpenAsync(db));
            Assert.Equal(["Family Parent"], (await ReaderRules.OthersAsync(db, childId)).Select(reader => reader.Name));
            Assert.Equal(AccountProblem.NotAllowed, await ReaderRules.RemoveSelfAsync(db, scope.ServiceProvider.GetRequiredService<EbookStore>(), scope.ServiceProvider.GetRequiredService<AudioStore>(), scope.ServiceProvider.GetRequiredService<CoverStore>(), childId, ShelfApiFactory.Password));
            Assert.Equal(AccountProblem.NotAllowed, await ReaderRules.SetAdminAsync(db, childId, true));
        }

        var own = (await (await kid.PostAsJsonAsync("/books", new CreateBookRequest("My Own Book", "Me", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await kid.PostAsJsonAsync($"/books/{own.Id}/lend", new LendRequest(parentId), JsonOptions)).StatusCode);

        // Other readers do not see the child, nor lend to them; the parent can.
        await using (var scope = Scope(neighbourId))
        {
            Assert.DoesNotContain("Family Kid", (await ReaderRules.OthersAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), neighbourId)).Select(reader => reader.Name));
        }

        var theirs = (await (await neighbour.PostAsJsonAsync("/books", new CreateBookRequest("Neighbour's Book", "Someone", BookStatus.Finished, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await neighbour.PostAsJsonAsync($"/books/{theirs.Id}/lend", new LendRequest(childId), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await parent.PostAsJsonAsync($"/books/{kept.Id}/lend", new LendRequest(childId), JsonOptions)).StatusCode);

        // The parent sets a new password, and can remove the account; an admin can make it an ordinary one.
        await using (var scope = Scope(parentId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.Null(await Family.SetChildPasswordAsync(db, childId, "another long password"));
            Assert.Equal(AccountProblem.NoSuchReader, await Family.SetChildPasswordAsync(db, neighbourId, "another long password"));
            Assert.True(await Family.ReleaseAsync(db, childId));
            Assert.Empty(await Family.ChildrenAsync(db));
        }
    }

    // A child reads a shared book as a borrower does: with a place and highlights of their own, kept, shown, and
    // removable, never mixed with the guardian's; and once the book is no longer shared, it closes to them.
    [Fact]
    public async Task A_child_keeps_their_own_place_and_highlights_in_a_shared_book()
    {
        var parent = await factory.SignUpAsync("Reading Parent");
        var parentId = await factory.ReaderIdAsync("Reading Parent");
        await using (var scope = Scope(parentId))
        {
            Assert.Null((await Family.CreateChildAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), "Reading Kid", ShelfApiFactory.Password)).Problem);
        }

        var kid = factory.CreateClient();
        await ShelfApiFactory.PostFormAsync(kid, "/signin", "/account/signin", new() { ["name"] = "Reading Kid", ["password"] = ShelfApiFactory.Password });
        var book = (await (await parent.PostAsJsonAsync("/books", new CreateBookRequest("Shared Pages", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(KoreaderHighlightsTests.Epub()), "file", "shared.epub" } })
        {
            Assert.True((await parent.PostAsync($"/books/{book.Id}/ebook", content)).IsSuccessStatusCode);
        }

        int collectionId;
        await using (var scope = Scope(parentId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var (collection, _) = await Collections.CreateAsync(db, "Kid's shelf", null);
            await Collections.AddAsync(db, collection!.Id, book.Id);
            await Family.SetSharedAsync(db, collection.Id, true);
            collectionId = collection.Id;
        }

        await parent.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("Before.", 0, "The parent's note."), JsonOptions);
        Assert.Empty(await kid.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions) ?? []);

        // The child's highlight is theirs: shown to them, kept from the parent, and theirs to remove.
        Assert.Equal(HttpStatusCode.Created, (await kid.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("After.", 0, "The child's note."), JsonOptions)).StatusCode);
        var mine = Assert.Single(await kid.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions) ?? []);
        Assert.Equal("The child's note.", mine.Note);
        Assert.Equal("The parent's note.", Assert.Single(await parent.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions) ?? []).Note);

        // Their place is kept, apart from the parent's, which stays at the start the upload gave it.
        Assert.Equal(HttpStatusCode.NoContent, (await kid.PutAsJsonAsync($"/books/{book.Id}/place", new PlaceRequest(2), JsonOptions)).StatusCode);
        Assert.Equal(2, (await kid.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);
        Assert.Equal(0, (await parent.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);

        Assert.Equal(HttpStatusCode.NoContent, (await kid.DeleteAsync($"/books/{book.Id}/highlights/{mine.Id}")).StatusCode);
        Assert.Empty(await kid.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions) ?? []);

        // No longer shared, the book closes to the child.
        await using (var scope = Scope(parentId))
        {
            await Family.SetSharedAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), collectionId, false);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await kid.GetAsync($"/books/{book.Id}/place")).StatusCode);
    }

    private AsyncServiceScope Scope(int readerId)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope;
    }
}
