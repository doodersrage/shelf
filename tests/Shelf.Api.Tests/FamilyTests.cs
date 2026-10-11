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

    private AsyncServiceScope Scope(int readerId)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope;
    }
}
