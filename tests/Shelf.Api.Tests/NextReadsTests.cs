using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class NextReadsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Suggestions_come_from_series_loved_authors_subjects_and_open_shelves()
    {
        var reader = await factory.SignUpAsync("Next Reader");
        var lender = await factory.SignUpAsync("Next Lender");
        async Task<BookResponse> Add(HttpClient client, CreateBookRequest request) =>
            (await (await client.PostAsJsonAsync("/books", request, JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;

        await Add(reader, new CreateBookRequest("A Wizard of Earthsea", "Ursula K. Le Guin", BookStatus.Finished, 5, Tags: ["fantasy", "magic"], Series: "Earthsea", SeriesNumber: 1));
        var tombs = await Add(reader, new CreateBookRequest("The Tombs of Atuan", "Ursula K. Le Guin", BookStatus.Want, null, Series: "Earthsea", SeriesNumber: 2));
        await Add(reader, new CreateBookRequest("The Farthest Shore", "Ursula K. Le Guin", BookStatus.Want, null, Series: "Earthsea", SeriesNumber: 3));
        await Add(reader, new CreateBookRequest("The Dispossessed", "Ursula K. Le Guin", BookStatus.Want, null));
        await Add(reader, new CreateBookRequest("Jonathan Strange", "Susanna Clarke", BookStatus.Want, null, Tags: ["magic"]));
        await Add(reader, new CreateBookRequest("Queued Up", "Someone", BookStatus.Want, null, Queued: true));
        await Add(reader, new CreateBookRequest("Nothing In Common", "Someone Else", BookStatus.Want, null));
        await Add(lender, new CreateBookRequest("The Lathe of Heaven", "Ursula K. Le Guin", BookStatus.Finished, null));
        await Add(lender, new CreateBookRequest("A Wizard of Earthsea", "Ursula K. Le Guin", BookStatus.Finished, null));
        // Tagged as the reader loves, by no author they love: the lender's tags are the lender's own, never weighed.
        await Add(lender, new CreateBookRequest("Tagged Like Yours", "Nobody Loved", BookStatus.Want, null, Tags: ["magic"]));
        await lender.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(true), JsonOptions);

        var next = await reader.GetFromJsonAsync<NextReads>("/books/next", JsonOptions);

        var series = Assert.Single(next!.Series);
        Assert.Equal((tombs.Id, NextReason.NextInSeries, "A Wizard of Earthsea"), (series.BookId, series.Reason, series.Because));
        Assert.Equal(["Queued Up", "The Dispossessed", "Jonathan Strange"], next.Shelf.Select(read => read.Title));
        Assert.Equal((NextReason.Author, "A Wizard of Earthsea"), (next.Shelf[1].Reason, next.Shelf[1].Because));
        Assert.Equal(NextReason.Subjects, next.Shelf[2].Reason);
        Assert.Equal(["magic"], next.Shelf[2].Subjects);

        // From the open shelf: the one by a loved author not already here, and none for its owner's tags.
        var borrow = Assert.Single(next.Borrowable);
        Assert.Equal(("The Lathe of Heaven", "Next Lender"), (borrow.Title, borrow.Owner));
        Assert.Empty(borrow.Subjects);
    }
}
