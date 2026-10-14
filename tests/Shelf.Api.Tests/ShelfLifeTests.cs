using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

// The rest of a reading life: libraries brought in by recipe, what a reader's reading says of them, the household's
// calendar, research across books, words kept in other languages, a sandbox to try connectors on, and spines read
// from a photo.
public sealed class ShelfLifeTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_spreadsheet_from_anywhere_comes_in_by_a_recipe_that_is_kept_for_next_time()
    {
        var reader = await factory.SignUpAsync("Recipe Reader");
        const string csv = "Book Name,Written By,Shelf,Score (0-10),Pages,Genres,Finished\n"
            + "The Left Hand of Darkness,Ursula K. Le Guin,gelesen,9,304,sf; classics,14.03.2021\n"
            + "Kindred,Octavia E. Butler,wishlist,,264,sf,\n"
            + ",No Title,gelesen,,,,\n";

        // The columns it can, guessed from their names; the rest chosen.
        var preview = (await (await reader.PostAsync("/books/import/recipes/preview", Form(csv))).Content.ReadFromJsonAsync<RecipePreview>(JsonOptions))!;
        Assert.Equal(3, preview.Total);
        Assert.Equal("Pages", preview.Spec.Columns["Pages"]);
        Assert.Equal("Genres", preview.Spec.Columns["Tags"]);
        Assert.Equal(["gelesen", "wishlist"], preview.StatusWords);

        var spec = new RecipeSpec(
            new() { ["Title"] = "Book Name", ["Author"] = "Written By", ["Status"] = "Shelf", ["Rating"] = "Score (0-10)", ["Pages"] = "Pages", ["Tags"] = "Genres", ["FinishedOn"] = "Finished" },
            new() { ["wishlist"] = BookStatus.Want }, ";", "dd.MM.yyyy", RatingOutOfTen: true);
        var shown = (await (await reader.PostAsync("/books/import/recipes/preview", Form(csv, spec))).Content.ReadFromJsonAsync<RecipePreview>(JsonOptions))!;
        Assert.Equal([(BookStatus?)BookStatus.Finished, BookStatus.Want, BookStatus.Finished], shown.Rows.Select(row => row.Status));
        Assert.NotNull(shown.Rows[2].Problem);

        var result = (await (await reader.PostAsync("/books/import/recipes/import", Form(csv, spec, saveAs: "My old app"))).Content.ReadFromJsonAsync<RecipeImportResult>(JsonOptions))!;
        Assert.Equal((2, 0), (result.Added, result.Skipped));
        var books = (await reader.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!;
        var left = books.Single(book => book.Title == "The Left Hand of Darkness");
        Assert.Equal((BookStatus.Finished, (int?)5, (DateOnly?)new DateOnly(2021, 3, 14), 304), (left.Status, left.Rating, left.FinishedOn, left.Pages ?? 0));
        Assert.Equal(["classics", "sf"], left.Tags.Order());

        // Kept by name, used again by id; the same books are skipped.
        var recipe = Assert.Single((await reader.GetFromJsonAsync<RecipeView[]>("/books/import/recipes", JsonOptions))!);
        Assert.Equal("My old app", recipe.Name);
        using var again = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "books.csv" }, { new StringContent(recipe.Id.ToString()), "recipeId" } };
        Assert.Equal(2, (await (await reader.PostAsync("/books/import/recipes/import", again)).Content.ReadFromJsonAsync<RecipeImportResult>(JsonOptions))!.Skipped);
        Assert.Empty((await (await factory.SignUpAsync("Recipe Bystander")).GetFromJsonAsync<RecipeView[]>("/books/import/recipes", JsonOptions))!);

        // An import by recipe is undone like any other.
        await reader.PostAsync($"/books/changes/{result.BatchId}/undo", null);
        Assert.Empty((await reader.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!);
    }

    [Fact]
    public void Reading_dna_finds_what_a_reader_returns_to_leaves_alone_and_has_forgotten()
    {
        var today = new DateOnly(2026, 10, 13);
        var id = 0;
        Book Make(string title, string author, BookStatus status, string[] tags, int? finishedYear = null, bool loved = false, int addedYearsAgo = 0, string? series = null, int? number = null) => new()
        {
            Id = ++id, Title = title, Author = author, Status = status, Loved = loved, Series = series, SeriesNumber = number,
            FinishedOn = finishedYear is int year ? new DateOnly(year, 6, 1) : null,
            AddedAt = new DateTimeOffset(today.AddYears(-addedYearsAgo).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            Tags = [.. tags.Select(tag => new Tag { Name = tag })],
        };

        var books = new List<Book>
        {
            Make("A", "Ursula K. Le Guin", BookStatus.Finished, ["fantasy"], 2019, loved: true, series: "Earthsea", number: 1),
            Make("B", "Ursula K. Le Guin", BookStatus.Finished, ["fantasy"], 2019),
            Make("C", "N. K. Jemisin", BookStatus.Finished, ["fantasy"], 2020),
            Make("D", "Someone", BookStatus.Finished, ["fantasy"], 2021),
            Make("E", "Someone Else", BookStatus.Finished, ["fantasy", "history"], 2021),
            Make("F", "Historian", BookStatus.Finished, ["history"], 2025),
            Make("G", "Historian", BookStatus.Finished, ["history"], 2025),
            Make("H", "Historian Two", BookStatus.Finished, ["history"], 2026),
            Make("I", "Historian Three", BookStatus.Finished, ["history"], 2026),
            Make("J", "Fourth", BookStatus.Finished, ["history"], 2026),
            Make("P1", "Poet", BookStatus.Want, ["poetry"], addedYearsAgo: 1),
            Make("P2", "Poet", BookStatus.Want, ["poetry"], addedYearsAgo: 1),
            Make("P3", "Poet", BookStatus.Want, ["poetry"], addedYearsAgo: 1),
            Make("The Tombs of Atuan", "Ursula K. Le Guin", BookStatus.Want, ["fantasy"], addedYearsAgo: 4, series: "Earthsea", number: 2),
            Make("Nothing Alike", "Stranger", BookStatus.Want, ["cookery"], addedYearsAgo: 6),
        };

        var dna = ReadingDna.Build(books, today);
        Assert.Equal(10, dna.Read);
        Assert.Equal(["history", "fantasy"], dna.Themes.Take(2).Select(strand => strand.Name).Order().Reverse());
        Assert.Contains("history", dna.Rising);
        Assert.Contains("fantasy", dna.Fading);
        Assert.Contains(dna.Unexplored, gap => gap.Name == "poetry" && gap.Unread == 3 && gap.Read == 0);
        Assert.Contains(dna.Unexplored, gap => gap.Name == "Poet");

        // Long waiting, and fitting: the next Earthsea book, by an author loved, about a subject read often.
        var forgotten = dna.Forgotten.First();
        Assert.Equal("The Tombs of Atuan", forgotten.Title);
        Assert.Equal(4, forgotten.Reasons.Length);
        Assert.DoesNotContain(dna.Forgotten, book => book.Title == "Nothing Alike");
    }

    [Fact]
    public async Task The_calendar_holds_a_readers_loans_meetings_plans_and_goals_and_no_one_elses()
    {
        var reader = await factory.SignUpAsync("Calendar Keeper");
        var friend = await factory.SignUpAsync("Calendar Friend");
        var friendId = await factory.ReaderIdAsync("Calendar Friend");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var lent = await CreateAsync(reader, new CreateBookRequest("Lent Book", "A. Writer", BookStatus.Finished, null, LoanedTo: "Kim", LoanedOn: today, DueOn: today.AddDays(7)));
        var mine = await CreateAsync(reader, new CreateBookRequest("To Lend", "A. Writer", BookStatus.Finished, null));
        await reader.PostAsJsonAsync($"/books/{mine.Id}/lend", new LendRequest(friendId, today.AddDays(10)), JsonOptions);
        var collection = await (await reader.PostAsJsonAsync("/books/collections", new { name = "Winter reading" }, JsonOptions)).Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        await reader.PutAsJsonAsync($"/books/collections/{collection.GetProperty("id").GetInt32()}/plan", new CollectionPlanRequest(today.AddDays(40)), JsonOptions);
        await reader.PostAsJsonAsync("/books/goals", new { kind = "Tag", name = "winter", target = 3 }, JsonOptions);
        var club = await (await reader.PostAsJsonAsync("/books/clubs", new { name = "Calendar club" }, JsonOptions)).Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var clubId = club.GetProperty("id").GetInt32();
        await reader.PostAsJsonAsync($"/books/clubs/{clubId}/books", new { bookId = mine.Id }, JsonOptions);
        var clubBook = (await reader.GetFromJsonAsync<JsonElement>($"/books/clubs/{clubId}", JsonOptions)).GetProperty("books")[0].GetProperty("id").GetInt32();
        var at = new DateTimeOffset(today.AddDays(5).ToDateTime(new TimeOnly(18, 30)), TimeSpan.Zero);
        (await reader.PostAsJsonAsync($"/books/clubs/{clubId}/meetings", new { clubBookId = clubBook, at, through = "Part one" }, JsonOptions)).EnsureSuccessStatusCode();

        var items = (await reader.GetFromJsonAsync<CalendarItem[]>("/books/agenda", JsonOptions))!;
        Assert.Equal([CalendarKind.Meeting, CalendarKind.Lent, CalendarKind.Lent, CalendarKind.Plan, CalendarKind.Goal], items.Select(item => item.Kind));
        Assert.Contains("Lent Book", items[1].Text);
        Assert.Equal(at, items[0].At);

        // The friend sees the book they have to give back, and nothing of the reader's own.
        var theirs = (await friend.GetFromJsonAsync<CalendarItem[]>("/books/agenda", JsonOptions))!;
        Assert.Equal(CalendarKind.Borrowed, Assert.Single(theirs).Kind);

        // The subscribed calendar has the meeting at its moment.
        string token;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Calendar Keeper"));
            token = await Feeds.MakeCalendarAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>());
        }

        var ics = await factory.CreateClient().GetStringAsync($"/calendar/{token}.ics");
        Assert.Contains($"DTSTART:{at.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}", ics);
        Assert.Contains("Winter reading", ics);
        Assert.Contains(lent.Title, ics);
    }

    [Fact]
    public async Task A_question_gathers_notes_across_books_and_comes_out_as_a_bibliography_and_a_packet()
    {
        var reader = await factory.SignUpAsync("Researcher");
        var one = await CreateAsync(reader, new CreateBookRequest("The Dispossessed", "Ursula K. Le Guin", BookStatus.Finished, 5, Year: 1974, Publisher: "Harper & Row", Subtitle: "An Ambiguous Utopia"));
        var two = await CreateAsync(reader, new CreateBookRequest("Mutual Aid", "Peter Kropotkin", BookStatus.Reading, null, Year: 1902, Publisher: "Heinemann"));
        var quote = await (await reader.PostAsJsonAsync($"/books/{one.Id}/quotes", new CreateQuoteRequest("You cannot buy the revolution.", 301), JsonOptions)).Content.ReadFromJsonAsync<QuoteResponse>(JsonOptions);

        var made = await reader.PostAsJsonAsync("/books/research", new ResearchTopicRequest("How do stateless societies keep order?", "Start with the fiction."), JsonOptions);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var topic = (await made.Content.ReadFromJsonAsync<ResearchTopicSummary>(JsonOptions))!.Id;
        Assert.Equal(HttpStatusCode.OK, (await reader.PostAsync($"/books/research/{topic}/quotes/{quote!.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.PostAsJsonAsync($"/books/research/{topic}/notes", new ResearchNoteRequest(two.Id, "ch. 3", null, "Cooperation as a factor of evolution.", "https://example.org/mutual-aid"), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync($"/books/research/{topic}/notes", new ResearchNoteRequest(null, null, null, null, "javascript:alert(1)"), JsonOptions)).StatusCode);

        var view = (await reader.GetFromJsonAsync<ResearchTopicView>($"/books/research/{topic}", JsonOptions))!;
        Assert.Equal(2, view.Entries.Length);
        Assert.Equal("p. 301", view.Entries[0].Place);
        Assert.Equal(["Kropotkin, Peter. *Mutual Aid*. Heinemann, 1902.", "Le Guin, Ursula K. *The Dispossessed: An Ambiguous Utopia*. Harper & Row, 1974."], view.Bibliography);
        var apa = (await reader.GetFromJsonAsync<ResearchTopicView>($"/books/research/{topic}?style=Apa", JsonOptions))!;
        Assert.Equal("Le Guin, U. K. (1974). *The Dispossessed: An Ambiguous Utopia*. Harper & Row.", apa.Bibliography[1]);
        var mla = (await reader.GetFromJsonAsync<ResearchTopicView>($"/books/research/{topic}?style=Mla", JsonOptions))!;
        Assert.Equal("Kropotkin, Peter. *Mutual Aid*. Heinemann, 1902.", mla.Bibliography[0]);

        var packet = await reader.GetStringAsync($"/books/research/{topic}/packet.md");
        Assert.StartsWith("# How do stateless societies keep order?", packet);
        Assert.Contains("> You cannot buy the revolution.\n> — p. 301", packet);
        Assert.Contains("## Mutual Aid, Peter Kropotkin", packet);
        Assert.Contains("- Le Guin, Ursula K.", packet);

        Assert.Equal(HttpStatusCode.NotFound, (await (await factory.SignUpAsync("Research Bystander")).GetAsync($"/books/research/{topic}")).StatusCode);
    }

    [Fact]
    public async Task Words_kept_while_reading_come_up_for_review_and_go_out_as_cards()
    {
        var reader = await factory.SignUpAsync("Language Learner");
        var book = await CreateAsync(reader, new CreateBookRequest("L'Étranger", "Albert Camus", BookStatus.Reading, null, Language: "French"));
        var kept = await (await reader.PostAsJsonAsync("/books/words", new WordRequest("aujourd'hui", Meaning: "today", Context: "Aujourd'hui, maman est morte.", BookId: book.Id), JsonOptions)).Content.ReadFromJsonAsync<WordView>(JsonOptions);
        Assert.Equal(("French", 0), (kept!.Language, kept.Box));

        // Kept again, it is the same word with what is new added.
        await reader.PostAsJsonAsync("/books/words", new WordRequest("aujourd'hui", Language: "French", Note: "adverb"), JsonOptions);
        var words = (await reader.GetFromJsonAsync<WordView[]>("/books/words?due=true", JsonOptions))!;
        Assert.Equal(("today", "adverb"), (Assert.Single(words).Meaning, words[0].Note));

        var knew = (await (await reader.PostAsJsonAsync($"/books/words/{kept.Id}/review", new ReviewRequest(true), JsonOptions)).Content.ReadFromJsonAsync<WordView>(JsonOptions))!;
        Assert.Equal((1, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2)), (knew.Box, knew.DueOn));
        Assert.Empty((await reader.GetFromJsonAsync<WordView[]>("/books/words?due=true", JsonOptions))!);
        var missed = (await (await reader.PostAsJsonAsync($"/books/words/{kept.Id}/review", new ReviewRequest(false), JsonOptions)).Content.ReadFromJsonAsync<WordView>(JsonOptions))!;
        Assert.Equal(0, missed.Box);

        Assert.Equal(new LanguageCount("French", 1, 0, 0), Assert.Single((await reader.GetFromJsonAsync<LanguageCount[]>("/books/words/languages", JsonOptions))!));
        var cards = await reader.GetStringAsync("/books/words/cards.txt");
        Assert.Contains("aujourd'hui\ttoday\tAujourd'hui, maman est morte.\tFrench", cards);

        var passage = await reader.PostAsJsonAsync("/books/passages", new PassageRequest("Aujourd'hui, maman est morte.", "Mother died today.", book.Id, OriginalLanguage: "French", TranslationLanguage: "English", Place: "p. 1"), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, passage.StatusCode);
        Assert.Equal("Mother died today.", Assert.Single((await reader.GetFromJsonAsync<PassageView[]>($"/books/passages?bookId={book.Id}", JsonOptions))!).Translation);
        Assert.Empty((await (await factory.SignUpAsync("Language Bystander")).GetFromJsonAsync<WordView[]>("/books/words", JsonOptions))!);
    }

    [Fact]
    public async Task A_sandbox_token_sees_only_sample_books_and_no_other_reader()
    {
        var reader = await factory.SignUpAsync("Connector Builder");
        await CreateAsync(reader, new CreateBookRequest("My Real Book", "Real Author", BookStatus.Reading, null));
        Assert.Equal(HttpStatusCode.NoContent, (await reader.GetAsync("/account/sandbox")).StatusCode);

        var token = (await (await reader.PostAsJsonAsync("/account/sandbox/tokens", new SandboxTokenRequest("Home Assistant test"), JsonOptions)).Content.ReadFromJsonAsync<NewApiToken>(JsonOptions))!;
        var connector = factory.CreateClient();
        connector.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var samples = (await connector.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!;
        Assert.Equal(12, samples.Length);
        Assert.DoesNotContain(samples, book => book.Title == "My Real Book");

        // Writing goes to the samples; the real shelf never changes.
        (await connector.PostAsJsonAsync("/books", new CreateBookRequest("Written by the connector", "Bot", BookStatus.Want, null), JsonOptions)).EnsureSuccessStatusCode();
        Assert.Single((await reader.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!);
        Assert.Equal(13, (await reader.GetFromJsonAsync<SandboxInfo>("/account/sandbox", JsonOptions))!.Books);

        // It is no one others can see, lend to, or be lent by.
        Assert.DoesNotContain((await reader.GetFromJsonAsync<ReaderResponse[]>("/readers", JsonOptions))!, other => other.Name.Contains("Sandbox"));
        Assert.DoesNotContain((await connector.GetFromJsonAsync<ReaderResponse[]>("/readers", JsonOptions))!, other => other.Name == "Connector Builder");
        var readerId = await factory.ReaderIdAsync("Connector Builder");
        var lend = await connector.PostAsJsonAsync($"/books/{samples[0].Id}/lend", new LendRequest(readerId), JsonOptions);
        Assert.NotEqual(HttpStatusCode.OK, lend.StatusCode);
        // A token never opens the account, so it cannot make tokens or sandboxes of its own.
        Assert.NotEqual(HttpStatusCode.OK, (await connector.PostAsJsonAsync("/account/sandbox/tokens", new SandboxTokenRequest("Nested"), JsonOptions)).StatusCode);

        // Reset: the samples as they were, and the token still works.
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync("/account/sandbox/reset", null)).StatusCode);
        Assert.Equal(12, (await connector.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!.Length);

        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync("/account/sandbox")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await connector.GetAsync("/books")).StatusCode);
    }

    [Fact]
    public void Lines_read_off_spines_are_matched_to_books_forgiving_a_slip_of_a_letter()
    {
        List<(int, string, string, string?)> books =
        [
            (1, "The Left Hand of Darkness", "Ursula K. Le Guin", "Study"),
            (2, "Dune", "Frank Herbert", "Study"),
            (3, "The Name of the Wind", "Patrick Rothfuss", "Hall"),
            (4, "Darkness at Noon", "Arthur Koestler", "Study"),
            (5, "Emma", "Jane Austen", "Study"),
        ];
        var found = ShelfPhotos.Match(["LE GUIN THE LEFT HAND OF DARKNES", "DUNE HERBERT", "Rothfuss", "The Name of the", "Wind", "EMMA", "A Cookbook of Soups"], books);
        Assert.Equal([1, 2, 3], found.Select(item => item.Match.BookId).Order());
        Assert.Equal("guin", ShelfPhotos.Surname("Le Guin, Ursula K."));
        Assert.Equal("guin", ShelfPhotos.Surname("Ursula K. Le Guin"));
    }

    [Fact]
    public async Task A_photo_of_a_shelf_ticks_off_the_books_it_shows_in_a_check()
    {
        var tools = factory.Services.GetRequiredService<OcrTools>();
        if (!tools.Available || !Installed("convert"))
        {
            return;
        }

        var reader = await factory.SignUpAsync("Photo Checker");
        var book = await CreateAsync(reader, new CreateBookRequest("The Left Hand of Darkness", "Ursula K. Le Guin", BookStatus.Finished, null, Location: "Attic"));
        await CreateAsync(reader, new CreateBookRequest("Middlemarch", "George Eliot", BookStatus.Want, null, Location: "Attic"));
        var session = (await (await reader.PostAsJsonAsync("/books/scans", new Scanning.ScanStart(ScanKind.Check, "Attic"), JsonOptions)).Content.ReadFromJsonAsync<ScanSessionView>(JsonOptions))!;

        var photo = await RenderAsync("THE LEFT HAND OF DARKNESS   LE GUIN\nA HISTORY OF SALT   KURLANSKY");
        using var content = new MultipartFormDataContent { { new ByteArrayContent(photo), "files", "shelf.png" }, { new StringContent(session.Id.ToString()), "scanId" } };
        var result = (await (await reader.PostAsync("/books/scans/photo", content)).Content.ReadFromJsonAsync<ShelfPhotoResult>(JsonOptions))!;
        Assert.True(result.CanRead);
        var match = Assert.Single(result.Matches);
        Assert.Equal((book.Id, true), (match.BookId, match.Seen));
        Assert.Contains(result.Unmatched, line => line.Contains("SALT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([book.Id], Assert.Single((await reader.GetFromJsonAsync<ScanSessionView[]>("/books/scans", JsonOptions))!).Seen);
    }

    [Fact]
    public void Tesseract_s_words_are_gathered_into_lines_with_how_sure_it_was()
    {
        var tsv = string.Join('\n',
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "4\t1\t1\t1\t1\t0\t0\t0\t9\t9\t-1\t",
            "5\t1\t1\t1\t1\t1\t0\t0\t9\t9\t96.5\tPIRANESI",
            "5\t1\t2\t1\t1\t1\t0\t0\t9\t9\t90\tSUSANNA",
            "5\t1\t2\t1\t1\t2\t0\t0\t9\t9\t80\tCLARKE",
            "5\t1\t3\t1\t1\t1\t0\t0\t9\t9\t41\tVNNVSNS\r");
        Assert.Equal([("PIRANESI", 96.5), ("SUSANNA CLARKE", 85.0), ("VNNVSNS", 41.0)], ShelfPhotos.Lines(tsv));
    }

    [Fact]
    public async Task Books_a_photo_found_are_noted_at_the_run_s_place_together_and_undone_together()
    {
        var reader = await factory.SignUpAsync("Spine Placer");
        var hall = await CreateAsync(reader, new CreateBookRequest("Piranesi", "Susanna Clarke", BookStatus.Finished, null, Location: "Hall"));
        var nowhere = await CreateAsync(reader, new CreateBookRequest("Emma", "Jane Austen", BookStatus.Want, null));
        var here = await CreateAsync(reader, new CreateBookRequest("Middlemarch", "George Eliot", BookStatus.Want, null, Location: "Study"));
        var check = (await (await reader.PostAsJsonAsync("/books/scans", new Scanning.ScanStart(ScanKind.Check, "Study"), JsonOptions)).Content.ReadFromJsonAsync<ScanSessionView>(JsonOptions))!;

        var placed = (await (await reader.PostAsJsonAsync($"/books/scans/{check.Id}/place", new ScanPlaceRequest([hall.Id, nowhere.Id, here.Id]), JsonOptions)).Content.ReadFromJsonAsync<ScanPlaced>(JsonOptions))!;
        Assert.NotNull(placed.BatchId);
        foreach (var id in new[] { hall.Id, nowhere.Id, here.Id })
        {
            Assert.Equal("Study", (await reader.GetFromJsonAsync<BookResponse>($"/books/{id}", JsonOptions))!.Location);
        }

        Assert.Equal([hall.Id, nowhere.Id, here.Id], Assert.Single((await reader.GetFromJsonAsync<ScanSessionView[]>("/books/scans", JsonOptions))!).Seen.Order());

        // Only the two that moved are in the change, and undoing it puts them back.
        Assert.Equal(HttpStatusCode.OK, (await reader.PostAsync($"/books/changes/{placed.BatchId}/undo", null)).StatusCode);
        Assert.Equal("Hall", (await reader.GetFromJsonAsync<BookResponse>($"/books/{hall.Id}", JsonOptions))!.Location);
        Assert.Null((await reader.GetFromJsonAsync<BookResponse>($"/books/{nowhere.Id}", JsonOptions))!.Location);
        Assert.Equal("Study", (await reader.GetFromJsonAsync<BookResponse>($"/books/{here.Id}", JsonOptions))!.Location);

        // An adding run notes them at its place but sees nothing; one with no place does nothing.
        var adding = (await (await reader.PostAsJsonAsync("/books/scans", new Scanning.ScanStart(ScanKind.Add, "Attic"), JsonOptions)).Content.ReadFromJsonAsync<ScanSessionView>(JsonOptions))!;
        await reader.PostAsJsonAsync($"/books/scans/{adding.Id}/place", new ScanPlaceRequest([nowhere.Id]), JsonOptions);
        Assert.Equal("Attic", (await reader.GetFromJsonAsync<BookResponse>($"/books/{nowhere.Id}", JsonOptions))!.Location);
        var unplaced = (await (await reader.PostAsJsonAsync("/books/scans", new Scanning.ScanStart(ScanKind.Add, null), JsonOptions)).Content.ReadFromJsonAsync<ScanSessionView>(JsonOptions))!;
        var none = (await (await reader.PostAsJsonAsync($"/books/scans/{unplaced.Id}/place", new ScanPlaceRequest([hall.Id]), JsonOptions)).Content.ReadFromJsonAsync<ScanPlaced>(JsonOptions))!;
        Assert.Null(none.BatchId);
        Assert.Equal("Hall", (await reader.GetFromJsonAsync<BookResponse>($"/books/{hall.Id}", JsonOptions))!.Location);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.PostAsJsonAsync("/books/scans/999999/place", new ScanPlaceRequest([hall.Id]), JsonOptions)).StatusCode);
    }

    private static bool Installed(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
            process?.WaitForExit(5000);
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<byte[]> RenderAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"spines-{Guid.NewGuid():N}.png");
        try
        {
            using var process = Process.Start(new ProcessStartInfo("convert", ["-size", "1600x300", "xc:white", "-fill", "black", "-pointsize", "40", "-annotate", "+40+110", text, path]))!;
            await process.WaitForExitAsync();
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static MultipartFormDataContent Form(string csv, RecipeSpec? spec = null, string? saveAs = null)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "books.csv" } };
        if (spec is not null)
        {
            form.Add(new StringContent(JsonSerializer.Serialize(spec, JsonOptions)), "spec");
        }

        if (saveAs is not null)
        {
            form.Add(new StringContent(saveAs), "saveAs");
        }

        return form;
    }

    private static async Task<BookResponse> CreateAsync(HttpClient client, CreateBookRequest request)
    {
        var response = await client.PostAsJsonAsync("/books", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
    }
}
