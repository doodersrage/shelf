using Microsoft.EntityFrameworkCore;
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
        // Read a chapter at a time, never the whole EPUB.
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/books/{book.Id}/ebook/file")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/books/{book.Id}/ebook/chapters/0")).StatusCode);
        Assert.Contains("opens a chapter at a time", await member.GetStringAsync($"/library/{book.Id}/read"));
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/books/{book.Id}/ebook/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/books/{adult.Id}/ebook/file")).StatusCode);
        // Nor through the e-reader catalog, nor the Audiobookshelf apps' own download.
        string key;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(memberId);
            key = await ReaderRules.NewKeyAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), memberId);
        }

        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"kobo:{key}")));
        Assert.Equal(HttpStatusCode.NotFound, (await device.GetAsync($"/opds/books/{book.Id}/file")).StatusCode);
        var app = factory.CreateClient();
        var login = await (await app.PostAsJsonAsync("/login", new { username = "Household Member", password = ShelfApiFactory.Password })).Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
        app.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login!["user"]!["accessToken"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync($"/api/items/{book.Id}/file/{AbsJson.EbookIno(book.Id)}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync($"/api/items/{book.Id}/ebook")).StatusCode);
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

    // A household book goes where a member's own do: into their playlists, the e-reader catalog when they may download,
    // KOReader sync with a place of their own, and their notes export with their own highlights.
    [Fact]
    public async Task Household_books_reach_playlists_the_catalog_koreader_and_notes()
    {
        var owner = await factory.SignUpAsync("Services Owner");
        var member = await factory.SignUpAsync("Services Member");
        var memberId = await factory.ReaderIdAsync("Services Member");
        var book = await BookAsync(owner, "Shared Everywhere");
        using (var content = new MultipartFormDataContent { { new StreamContent(BooksEndpointTests.ZipText("01.mp3", "a shared recording")), "file", "shared.zip" } })
        {
            await owner.PostAsync($"/books/{book.Id}/audio", content);
        }

        var collection = (await (await owner.PostAsJsonAsync("/books/collections", new CollectionRequest("Everywhere"), JsonOptions)).Content.ReadFromJsonAsync<CollectionResponse>(JsonOptions))!;
        await owner.PostAsJsonAsync($"/books/collections/{collection.Id}/books", new CollectionBookRequest(book.Id), JsonOptions);
        await owner.PostAsJsonAsync($"/books/collections/{collection.Id}/members", new MemberRequest(memberId), JsonOptions);

        // A playlist of the member's own holds it, and shows it by its title.
        var list = (await (await member.PostAsJsonAsync("/books/playlists", new PlaylistRequest("Mine"), JsonOptions)).Content.ReadFromJsonAsync<PlaylistSummary>(JsonOptions))!;
        Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsJsonAsync($"/books/playlists/{list.Id}/items", new PlaylistAddRequest(book.Id, null), JsonOptions)).StatusCode);
        var entry = Assert.Single((await member.GetFromJsonAsync<PlaylistView>($"/books/playlists/{list.Id}", JsonOptions))!.Items);
        Assert.Equal(("Shared Everywhere", false, $"/library/{book.Id}/listen"), (entry.Title, entry.Gone, entry.Link));

        // The catalog's Shared with you, while downloads are allowed.
        string key;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(memberId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            key = await ReaderRules.NewKeyAsync(db, memberId);
            (await db.Readers.FirstAsync(item => item.Id == memberId)).KosyncHash = Kosync.HashOf("koreader-secret");
            await db.SaveChangesAsync();
        }

        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"kobo:{key}")));
        Assert.Contains($"/opds/books/{book.Id}/file", await device.GetStringAsync("/opds/shared"));
        var membership = Assert.Single((await owner.GetFromJsonAsync<HouseholdMember[]>($"/books/collections/{collection.Id}/members", JsonOptions))!);
        await owner.PutAsJsonAsync($"/books/collections/{collection.Id}/members/{membership.Id}", new MemberChange(CanDownload: false, HideExplicit: false), JsonOptions);
        Assert.DoesNotContain($"/opds/books/{book.Id}/file", await device.GetStringAsync("/opds/shared"));

        // KOReader keeps the member's own place in it, and leaves the owner's.
        string digest;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            digest = (await scope.ServiceProvider.GetRequiredService<ShelfDb>().Books.IgnoreQueryFilters().FirstAsync(item => item.Id == book.Id)).KoreaderDigest!;
        }

        var koreader = factory.CreateClient();
        koreader.DefaultRequestHeaders.Accept.ParseAdd(Kosync.AcceptType);
        koreader.DefaultRequestHeaders.Add("x-auth-user", "Services Member");
        koreader.DefaultRequestHeaders.Add("x-auth-key", Convert.ToHexString(System.Security.Cryptography.MD5.HashData("koreader-secret"u8.ToArray())).ToLowerInvariant());
        var put = await koreader.PutAsJsonAsync("/kosync/syncs/progress", new { document = digest, progress = "/body/DocFragment[1]/body", percentage = 0.5, device = "Kobo", device_id = "kobo-1" });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        Assert.Contains((await member.GetFromJsonAsync<PositionsOfBook>($"/books/{book.Id}/positions", JsonOptions))!.Reports, report => report.Source == "KOReader");
        Assert.DoesNotContain((await owner.GetFromJsonAsync<PositionsOfBook>($"/books/{book.Id}/positions", JsonOptions))!.Reports, report => report.Source == "KOReader");

        // The member's notes export carries their highlight in it.
        await member.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("The text", 0, "A member's thought for the export."), JsonOptions);
        using var notes = new System.IO.Compression.ZipArchive(new MemoryStream(await member.GetByteArrayAsync("/books/notes.zip")));
        var file = Assert.Single(notes.Entries);
        using var text = new StreamReader(file.Open());
        Assert.Contains("A member's thought for the export.", await text.ReadToEndAsync());
    }
}
