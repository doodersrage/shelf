using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class TidyTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData("Ursula K. Le Guin", "ursula k le guin")]
    [InlineData("Le Guin, Ursula K.", "ursula k le guin")]
    [InlineData("URSULA K LE GUIN", "ursula k le guin")]
    [InlineData("Gabriel García Márquez", "gabriel garcia marquez")]
    [InlineData("Terry Pratchett & Neil Gaiman", "terry pratchett neil gaiman")]
    public void An_author_is_known_however_written(string written, string key) => Assert.Equal(key, Tidy.AuthorKey(written));

    [Theory]
    [InlineData("The Tombs of Atuan (Earthsea Cycle, #2)", "The Tombs of Atuan", "Earthsea Cycle", 2)]
    [InlineData("Guards! Guards! (Discworld Book 8)", "Guards! Guards!", "Discworld", 8)]
    [InlineData("Discworld 08 - Guards! Guards!", "Guards! Guards!", "Discworld", 8)]
    [InlineData("The Expanse #3: Abaddon's Gate", "Abaddon's Gate", "The Expanse", 3)]
    public void A_series_is_found_inside_a_title(string title, string newTitle, string series, int number)
    {
        var found = Tidy.FindSeries(1, title);
        Assert.Equal((newTitle, series, number), (found!.NewTitle, found.Series, found.Number));
    }

    [Theory]
    [InlineData("1984 - A Novel")]
    [InlineData("Catch-22")]
    [InlineData("A Wizard of Earthsea")]
    public void A_title_without_a_series_is_left_alone(string title) => Assert.Null(Tidy.FindSeries(1, title));

    [Fact]
    public async Task Spellings_become_one_and_series_come_out_of_titles()
    {
        var client = await factory.SignUpAsync("Tidy Reader");
        foreach (var (title, author) in new[] { ("A Wizard of Earthsea", "Ursula K. Le Guin"), ("The Tombs of Atuan (Earthsea Cycle, #2)", "Le Guin, Ursula K."), ("Tehanu", "Ursula K Le Guin"), ("Kindred", "Octavia E. Butler") })
        {
            await client.PostAsJsonAsync("/books", new CreateBookRequest(title, author, BookStatus.Want, null), JsonOptions);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Tidy Reader"));
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var group = Assert.Single(await Tidy.AuthorGroupsAsync(db));
        Assert.Equal(3, group.Spellings.Count);
        // Made as the Tidy page makes it, under a batch, and undone from Trash & undo, as the page promises.
        var batch = await History.StartAsync(db, BatchKind.Edit, "Authors");
        using (History.Use(batch.Id))
        {
            Assert.Equal(2, await Tidy.UseSpellingAsync(db, [.. group.Spellings.Select(item => item.Name)], "Ursula K. Le Guin"));
        }

        Assert.Empty(await Tidy.AuthorGroupsAsync(db));
        var stores = scope.ServiceProvider;
        Assert.NotNull(await History.UndoAsync(db, stores.GetRequiredService<EbookStore>(), stores.GetRequiredService<AudioStore>(), stores.GetRequiredService<CoverStore>(), batch.Id));
        Assert.Equal(3, Assert.Single(await Tidy.AuthorGroupsAsync(db)).Spellings.Count);
        Assert.Equal(2, await Tidy.UseSpellingAsync(db, [.. group.Spellings.Select(item => item.Name)], "Ursula K. Le Guin"));
        Assert.Empty(await Tidy.AuthorGroupsAsync(db));

        var series = Assert.Single(await Tidy.SeriesInTitlesAsync(db));
        Assert.Equal(1, await Tidy.ApplySeriesAsync(db, [series]));
        var books = (await client.GetFromJsonAsync<List<BookResponse>>("/books", JsonOptions))!;
        var tombs = books.Single(book => book.Title == "The Tombs of Atuan");
        Assert.Equal(("Earthsea Cycle", 2, "Ursula K. Le Guin"), (tombs.Series, tombs.SeriesNumber, tombs.Author));
        Assert.Contains("Series inside titles", await client.GetStringAsync("/tidy"));
    }

    [Fact]
    public async Task Details_are_filled_in_the_background_a_book_at_a_time()
    {
        var client = await factory.SignUpAsync("Detail Reader");
        await client.PostAsJsonAsync("/books", new CreateBookRequest("A Wizard of Earthsea", "Ursula K. Le Guin", BookStatus.Want, null), JsonOptions);
        await client.PostAsJsonAsync("/books", new CreateBookRequest("Nothing Known", "Nobody", BookStatus.Want, null), JsonOptions);

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Detail Reader"));
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var filler = factory.Services.GetRequiredService<DetailsFiller>();
        var job = (await filler.StartAsync(db, DetailsKind.Everything))!;
        Assert.Same(job, await filler.StartAsync(db, DetailsKind.Everything));
        for (var attempt = 0; attempt < 200 && !job.Finished; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(job.Finished);
        var lines = job.Lines();
        Assert.Equal(ImportLineState.Done, lines.Single(line => line.Title == "A Wizard of Earthsea").State);
        Assert.Contains("cover", lines.Single(line => line.Title == "A Wizard of Earthsea").Note);
        Assert.Equal(ImportLineState.Skipped, lines.Single(line => line.Title == "Nothing Known").State);
        var earthsea = (await client.GetFromJsonAsync<List<BookResponse>>("/books", JsonOptions))!.Single(book => book.Title == "A Wizard of Earthsea");
        Assert.Equal((1968, 205), (earthsea.Year, earthsea.Pages));
        Assert.Contains("Details filled in", await client.GetStringAsync("/activity"));
    }

    // A book the catalogs couldn't be asked about is marked failed, worth filling in again, not as matching nothing.
    [Fact]
    public async Task Details_say_when_the_catalogs_could_not_be_asked()
    {
        var client = await factory.SignUpAsync("Detail Outage Reader");
        await client.PostAsJsonAsync("/books", new CreateBookRequest("Out Of Reach", "Someone", BookStatus.Want, null), JsonOptions);
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddTransient<IBookLookup, UnreachableLookup>()));
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Detail Outage Reader"));
        var job = (await app.Services.GetRequiredService<DetailsFiller>().StartAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), DetailsKind.Everything))!;
        for (var attempt = 0; attempt < 200 && !job.Finished; attempt++)
        {
            await Task.Delay(50);
        }

        var line = job.Lines().Single(line => line.Title == "Out Of Reach");
        Assert.Equal((ImportLineState.Failed, "The catalogs did not answer."), (line.State, line.Note));
    }

    private sealed class UnreachableLookup : IBookLookup
    {
        public bool CouldNotAsk => true;

        public Task<CatalogMatch?> FindAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken) => Task.FromResult<CatalogMatch?>(null);
    }
}
