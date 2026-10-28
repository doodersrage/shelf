using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class MergingTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Look_alikes_are_found_and_merged_into_one_book()
    {
        var reader = await factory.SignUpAsync("Merging Reader");
        var paper = await CreateAsync(reader, new CreateBookRequest("The Left Hand of Darkness", "Ursula K. Le Guin", BookStatus.Finished, 5, Pages: 304, FinishedOn: new DateOnly(2024, 3, 1), Tags: ["sf"], Location: "Hall shelf"));
        var ebook = await CreateAsync(reader, new CreateBookRequest("The left hand of darkness", "Ursula K. Le Guin", BookStatus.Want, null, Year: 1969, Isbn: "9780441478125", Tags: ["classic"], Notes: "The ebook copy."));
        await CreateAsync(reader, new CreateBookRequest("A Different Book", "Ursula K. Le Guin", BookStatus.Want, null));
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(KoreaderHighlightsTests.Epub()), "file", "left-hand.epub" } })
        {
            Assert.True((await reader.PostAsync($"/books/{ebook.Id}/ebook", content)).IsSuccessStatusCode);
        }

        await reader.PostAsJsonAsync($"/books/{ebook.Id}/quotes", new { text = "Light is the left hand of darkness.", page = 233 }, JsonOptions);
        await reader.PostAsJsonAsync($"/books/{ebook.Id}/reading-time", new ReadingTimeRequest(null, 90), JsonOptions);
        await reader.PostAsJsonAsync($"/books/{paper.Id}/reading-time", new ReadingTimeRequest(null, 30), JsonOptions);
        var collection = (await (await reader.PostAsJsonAsync("/books/collections", new CollectionRequest("Classics"), JsonOptions)).Content.ReadFromJsonAsync<CollectionResponse>(JsonOptions))!;
        await reader.PostAsJsonAsync($"/books/collections/{collection.Id}/books", new CollectionBookRequest(ebook.Id), JsonOptions);

        var group = Assert.Single((await reader.GetFromJsonAsync<DuplicateGroup[]>("/books/duplicates", JsonOptions))!);
        Assert.Equal("title", group.Reason);
        Assert.Equal([paper.Id, ebook.Id], group.Books.Select(book => book.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync($"/books/{paper.Id}/merge", new MergeRequest(ebook.Id), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/books/{ebook.Id}")).StatusCode);
        var merged = (await reader.GetFromJsonAsync<BookResponse>($"/books/{paper.Id}", JsonOptions))!;
        Assert.Equal((BookStatus.Finished, 5, 304, 1969, "9780441478125", "Hall shelf", "The ebook copy."), (merged.Status, merged.Rating, merged.Pages, merged.Year, merged.Isbn, merged.Location, merged.Notes));
        Assert.Equal(["classic", "sf"], merged.Tags);
        Assert.Equal("left-hand.epub", merged.EbookFileName);
        Assert.Equal("Light is the left hand of darkness.", Assert.Single(merged.Quotes).Text);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/books/{paper.Id}/ebook/file")).StatusCode);
        Assert.Equal(120, (await reader.GetFromJsonAsync<ListeningSummary>("/books/reading-time", JsonOptions))!.TodaySeconds);
        Assert.Equal([paper.Id], (await reader.GetFromJsonAsync<CollectionResponse>($"/books/collections/{collection.Id}", JsonOptions))!.Books.Select(book => book.Id));
        Assert.Empty((await reader.GetFromJsonAsync<DuplicateGroup[]>("/books/duplicates", JsonOptions))!);
    }

    // A merge made by mistake comes apart: restoring the merged-away book from the trash takes back its file, tags,
    // quotes, highlights, place in collections, and reading time, and the kept book's details as they were, except
    // one changed since the merge, which is the reader's and stays.
    [Fact]
    public async Task Restoring_a_merged_away_book_takes_back_what_was_its_own()
    {
        var reader = await factory.SignUpAsync("Unmerging Reader");
        var readerId = await factory.ReaderIdAsync("Unmerging Reader");
        var paper = await CreateAsync(reader, new CreateBookRequest("The Dispossessed", "Ursula K. Le Guin", BookStatus.Finished, 5, Pages: 387, Tags: ["sf"]));
        var ebook = await CreateAsync(reader, new CreateBookRequest("The dispossessed", "Ursula K. Le Guin", BookStatus.Want, null, Year: 1974, Isbn: "9780061054884", Tags: ["utopia"], Notes: "The ebook copy."));
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(KoreaderHighlightsTests.Epub()), "file", "dispossessed.epub" } })
        {
            Assert.True((await reader.PostAsync($"/books/{ebook.Id}/ebook", content)).IsSuccessStatusCode);
        }

        await reader.PostAsJsonAsync($"/books/{ebook.Id}/quotes", new { text = "You cannot buy the revolution.", page = 301 }, JsonOptions);
        await reader.PostAsJsonAsync($"/books/{ebook.Id}/highlights", new CreateHighlightRequest("Before.", 0, "Marked in the ebook."), JsonOptions);
        await reader.PostAsJsonAsync($"/books/{ebook.Id}/reading-time", new ReadingTimeRequest(null, 90), JsonOptions);
        await reader.PostAsJsonAsync($"/books/{paper.Id}/reading-time", new ReadingTimeRequest(null, 30), JsonOptions);
        var collection = (await (await reader.PostAsJsonAsync("/books/collections", new CollectionRequest("Anarres"), JsonOptions)).Content.ReadFromJsonAsync<CollectionResponse>(JsonOptions))!;
        await reader.PostAsJsonAsync($"/books/collections/{collection.Id}/books", new CollectionBookRequest(ebook.Id), JsonOptions);

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync($"/books/{paper.Id}/merge", new MergeRequest(ebook.Id), JsonOptions)).StatusCode);
        var merged = (await reader.GetFromJsonAsync<BookResponse>($"/books/{paper.Id}", JsonOptions))!;
        Assert.Equal(("9780061054884", 1974, "dispossessed.epub"), (merged.Isbn, merged.Year, merged.EbookFileName));

        // After the merge, the reader corrects the year the merge filled in: that is theirs now.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
            (await db.Books.FirstAsync(book => book.Id == paper.Id)).Year = 1975;
            await db.SaveChangesAsync();
        }

        var trashed = Assert.Single(await reader.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions) ?? [], item => item.BookId == ebook.Id);
        var restore = (await (await reader.PostAsync($"/books/trash/{trashed.Id}/restore", null)).Content.ReadFromJsonAsync<TrashRestore>(JsonOptions))!;
        Assert.True(restore.Restored);
        var back = (await reader.GetFromJsonAsync<BookResponse>($"/books/{restore.BookId}", JsonOptions))!;
        var kept = (await reader.GetFromJsonAsync<BookResponse>($"/books/{paper.Id}", JsonOptions))!;

        // The restored book has what was its own again.
        Assert.Equal(("The dispossessed", "dispossessed.epub"), (back.Title, back.EbookFileName));
        Assert.Equal(["utopia"], back.Tags);
        Assert.Equal("You cannot buy the revolution.", Assert.Single(back.Quotes).Text);
        Assert.Equal("Marked in the ebook.", Assert.Single(await reader.GetFromJsonAsync<HighlightResponse[]>($"/books/{back.Id}/highlights", JsonOptions) ?? []).Note);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/books/{back.Id}/ebook/file")).StatusCode);
        Assert.Equal([back.Id], (await reader.GetFromJsonAsync<CollectionResponse>($"/books/collections/{collection.Id}", JsonOptions))!.Books.Select(book => book.Id));

        // The kept book is as it was, but for the year the reader changed.
        Assert.Equal(((string?)null, (int?)1975, (string?)null, (string?)null), (kept.Isbn, kept.Year, kept.Notes, kept.EbookFileName));
        Assert.Equal(["sf"], kept.Tags);
        Assert.Empty(kept.Quotes);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/books/{paper.Id}/ebook/file")).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Shelf.Api.Data.ShelfDb>();
            var days = await db.ReadingDays.IgnoreQueryFilters().Where(day => day.ReaderId == readerId).ToListAsync();
            Assert.Equal(30, days.Where(day => day.BookId == paper.Id).Sum(day => day.Seconds));
            Assert.Equal(90, days.Where(day => day.BookId == back.Id).Sum(day => day.Seconds));
        }
    }

    [Fact]
    public async Task A_book_on_loan_is_not_merged_away_nor_one_on_another_shelf()
    {
        var owner = await factory.SignUpAsync("Careful Merger");
        var other = await factory.SignUpAsync("Other Merger");
        var kept = await CreateAsync(owner, new CreateBookRequest("Lent Twice", "Someone", BookStatus.Want, null));
        var lent = await CreateAsync(owner, new CreateBookRequest("Lent Twice", "Someone", BookStatus.Want, null));
        var theirs = await CreateAsync(other, new CreateBookRequest("Lent Twice", "Someone", BookStatus.Want, null));
        await owner.PostAsJsonAsync($"/books/{lent.Id}/lend", new LendRequest(await factory.ReaderIdAsync("Other Merger")), JsonOptions);

        var refused = await owner.PostAsJsonAsync($"/books/{kept.Id}/merge", new MergeRequest(lent.Id), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("on loan", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/books/{kept.Id}/merge", new MergeRequest(theirs.Id), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/books/{kept.Id}/merge", new MergeRequest(kept.Id), JsonOptions)).StatusCode);
    }

    private static async Task<BookResponse> CreateAsync(HttpClient client, CreateBookRequest request) =>
        (await (await client.PostAsJsonAsync("/books", request, JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
}
