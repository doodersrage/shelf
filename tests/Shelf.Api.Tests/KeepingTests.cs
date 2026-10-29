using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

// Keeping a library over the years: where each copy is, through a move; what comes and goes; the queue; rules that
// act on their own; where each device has a reader; a download that proves itself; what each reader can see; a
// shelf asked in words; the plan for after; and word of a newer Shelf.
public sealed class KeepingTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Places_nest_take_their_copies_along_and_a_copy_away_for_a_time_remembers_home()
    {
        var reader = await factory.SignUpAsync("Place Keeper");
        var dune = await CreateAsync(reader, new CreateBookRequest("Dune", "Frank Herbert", BookStatus.Want, null, Location: "Study"));
        var emma = await CreateAsync(reader, new CreateBookRequest("Emma", "Jane Austen", BookStatus.Want, null, Location: "study › Shelf 3"));

        // The places copies name become places, spelled as the place is.
        Assert.Equal(2, (await (await reader.PostAsync("/books/storage/adopt", null)).Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).GetProperty("made").GetInt32());
        var map = (await reader.GetFromJsonAsync<PlaceMap>("/books/storage", JsonOptions))!;
        Assert.Empty(map.Loose);
        var study = map.Places.Single(place => place.Name == "Study");
        var shelf = map.Places.Single(place => place.Name == "Shelf 3");
        Assert.Equal((1, 2), (study.Here, study.Within));
        Assert.Equal("Study › Shelf 3", (await Book(reader, emma.Id)).Location);

        // Renamed, its copies' places follow, those within it too; the library's place filter takes in what is within.
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync($"/books/storage/{study.Id}", new StoragePlaceRequest("Library", PlaceKind.Room), JsonOptions)).StatusCode);
        Assert.Equal("Library › Shelf 3", (await Book(reader, emma.Id)).Location);
        Assert.Equal(2, (await reader.GetFromJsonAsync<BookResponse[]>("/books?place=Library", JsonOptions))!.Length);

        // A place can't be within itself, nor share a name with another beside it.
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PutAsJsonAsync($"/books/storage/{study.Id}", new StoragePlaceRequest("Library", PlaceKind.Room, shelf.Id), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/books/storage", new StoragePlaceRequest("Shelf 3", PlaceKind.Shelf, study.Id), JsonOptions)).StatusCode);

        // Into a storage unit for a time, a copy keeps where it lives, and goes back to it.
        var unit = (await (await reader.PostAsJsonAsync("/books/storage", new StoragePlaceRequest("Storage unit", PlaceKind.Other, Temporary: true), JsonOptions)).Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).GetProperty("id").GetInt32();
        await reader.PostAsJsonAsync("/books/storage/move", new StoragePlaceMove([dune.Id], unit), JsonOptions);
        var away = await Book(reader, dune.Id);
        Assert.Equal(("Storage unit", "Library"), (away.Location, away.Life?.HomeLocation));
        await reader.PostAsJsonAsync("/books/storage/put-back", new StoragePlaceMove([dune.Id], null), JsonOptions);
        var home = await Book(reader, dune.Id);
        Assert.Equal(("Library", (string?)null), (home.Location, home.Life?.HomeLocation));

        // Places are each reader's own.
        Assert.Empty((await (await factory.SignUpAsync("Place Stranger")).GetFromJsonAsync<PlaceMap>("/books/storage", JsonOptions))!.Places);
        Assert.Contains("Shelf labels", await reader.GetStringAsync("/places/labels"));
        Assert.Contains("Emma", await reader.GetStringAsync($"/places/{shelf.Id}"));
    }

    [Fact]
    public async Task A_box_is_packed_by_scanning_and_unpacked_where_it_was_going_with_what_never_arrived_named()
    {
        var reader = await factory.SignUpAsync("Mover");
        var kept = await CreateAsync(reader, new CreateBookRequest("Kept", "Someone", BookStatus.Want, null, Isbn: "9780441013593", Location: "Old flat"));
        var lost = await CreateAsync(reader, new CreateBookRequest("Lost", "Someone", BookStatus.Want, null, Isbn: "9780141439518", Location: "Old flat"));
        var room = (await (await reader.PostAsJsonAsync("/books/storage", new StoragePlaceRequest("New study", PlaceKind.Room), JsonOptions)).Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).GetProperty("id").GetInt32();
        var made = (await (await reader.PostAsJsonAsync("/books/storage", new StoragePlaceRequest("", PlaceKind.Box, DestinationId: room), JsonOptions)).Content.ReadFromJsonAsync<JsonElement>(JsonOptions));
        var box = made.GetProperty("path").GetString()!;
        Assert.Equal("Box 1", box);

        var pack = (await (await reader.PostAsJsonAsync("/books/scans", new Scanning.ScanStart(ScanKind.Pack, box), JsonOptions)).Content.ReadFromJsonAsync<ScanSessionView>(JsonOptions))!;
        foreach (var code in new[] { "9780441013593", "9780141439518" })
        {
            var read = (await (await reader.PostAsJsonAsync($"/books/scans/{pack.Id}/codes", new Scanning.ScanCode(code), JsonOptions)).Content.ReadFromJsonAsync<ScanCheck>(JsonOptions))!;
            Assert.Equal(ScanFinding.Here, read.Finding);
        }

        await reader.PostAsJsonAsync($"/books/scans/{pack.Id}/finish", new ScanFinishRequest(false), JsonOptions);
        var packed = await Book(reader, kept.Id);
        Assert.Equal((box, "Old flat"), (packed.Location, packed.Life?.HomeLocation));
        Assert.Contains("Kept", await reader.GetStringAsync("/places/labels?id=boxes"));

        // One comes out at the other end; the other is named as missing.
        var unpack = (await (await reader.PostAsJsonAsync("/books/scans", new Scanning.ScanStart(ScanKind.Unpack, box), JsonOptions)).Content.ReadFromJsonAsync<ScanSessionView>(JsonOptions))!;
        await reader.PostAsJsonAsync($"/books/scans/{unpack.Id}/codes", new Scanning.ScanCode("9780441013593"), JsonOptions);
        var finished = (await (await reader.PostAsJsonAsync($"/books/scans/{unpack.Id}/finish", new ScanFinishRequest(true), JsonOptions)).Content.ReadFromJsonAsync<ScanFinished>(JsonOptions))!;
        Assert.Equal((1, 1), (finished.Seen, finished.Missing));
        var arrived = await Book(reader, kept.Id);
        Assert.Equal(("New study", (string?)null), (arrived.Location, arrived.Life?.HomeLocation));
        Assert.Contains(Scanning.MissingTag, (await Book(reader, lost.Id)).Tags);
        Assert.Contains("unpacked", await reader.GetStringAsync("/moving"));
    }

    [Fact]
    public async Task A_copy_let_go_keeps_its_record_and_the_years_show_what_came_was_read_and_went()
    {
        var reader = await factory.SignUpAsync("Collector");
        var year = DateTime.UtcNow.Year;
        var sold = await CreateAsync(reader, new CreateBookRequest("Sold One", "Someone", BookStatus.Finished, 3, FinishedOn: new DateOnly(year, 1, 2), AcquiredOn: new DateOnly(year, 1, 1), PricePaid: 12.5m));
        await CreateAsync(reader, new CreateBookRequest("Waiting", "Someone", BookStatus.Want, null, PricePaid: 7.5m));

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync($"/books/{sold.Id}/let-go", new LetGoRequest(Departure.Sold, new DateOnly(year, 1, 3), "A shop", 4m), JsonOptions)).StatusCode);
        var gone = await Book(reader, sold.Id);
        Assert.Equal((Departure.Sold, 4m, "A shop"), (gone.Life!.LeftHow!.Value, gone.Life.SoldFor!.Value, gone.Life.LeftTo!));

        var report = (await reader.GetFromJsonAsync<LifecycleReport>("/books/lifecycle", JsonOptions))!;
        var now = report.Years.Single(item => item.Year == year);
        Assert.Equal((2, 1, 1, 1), (now.Came, now.Finished, now.Left, now.OwnedAtEnd));
        Assert.Equal((20m, 4m, 1), (report.Spent, report.Got, report.Unread));
        Assert.Contains("Your collection over time", await reader.GetStringAsync("/lifecycle"));

        // A goal of letting go counts it.
        await reader.PostAsJsonAsync("/books/goals", new ReadingGoalRequest(GoalKind.LetGo, null, 5), JsonOptions);
        Assert.Equal(1, Assert.Single((await reader.GetFromJsonAsync<GoalProgress[]>("/books/goals", JsonOptions))!).Done);

        // A copy out on loan can't go; one back is kept again.
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/{sold.Id}/let-go")).StatusCode);
        Assert.Null((await Book(reader, sold.Id)).Life?.LeftOn);
    }

    [Fact]
    public async Task The_queue_keeps_its_order_holds_wait_and_the_next_in_a_series_comes_first()
    {
        var reader = await factory.SignUpAsync("Queuer");
        var one = await CreateAsync(reader, new CreateBookRequest("Saga One", "Someone", BookStatus.Reading, null, Series: "Saga", SeriesNumber: 1));
        var two = await CreateAsync(reader, new CreateBookRequest("Saga Two", "Someone", BookStatus.Want, null, Series: "Saga", SeriesNumber: 2));
        var a = await CreateAsync(reader, new CreateBookRequest("Alpha", "Someone", BookStatus.Want, null));
        var b = await CreateAsync(reader, new CreateBookRequest("Beta", "Someone", BookStatus.Want, null));
        foreach (var id in new[] { a.Id, b.Id, two.Id })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync($"/books/queue/{id}", null)).StatusCode);
        }

        await reader.PutAsJsonAsync("/books/queue", new QueueOrderRequest([b.Id, a.Id]), JsonOptions);
        Assert.Equal([b.Id, a.Id, two.Id], (await reader.GetFromJsonAsync<QueueView>("/books/queue", JsonOptions))!.Ready.Select(book => book.Id));

        await reader.PutAsJsonAsync($"/books/queue/{b.Id}/hold", new HoldRequest(null, "for the winter"), JsonOptions);
        var view = (await reader.GetFromJsonAsync<QueueView>("/books/queue", JsonOptions))!;
        Assert.Equal([a.Id, two.Id], view.Ready.Select(book => book.Id));
        Assert.Equal("for the winter", Assert.Single(view.Held).HoldNote);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsync($"/books/queue/{one.Id}", null)).StatusCode);

        await using var scope = Scope(await factory.ReaderIdAsync("Queuer"));
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var finished = await db.Books.SingleAsync(book => book.Id == one.Id);
        Assert.Equal([two.Id, a.Id], (await ReadingQueue.UpNextAsync(db, finished)).Select(book => book.Id));
        Assert.Contains("Up next", await reader.GetStringAsync("/queue"));
    }

    [Fact]
    public async Task A_rule_acts_after_a_book_is_finished_can_be_tried_first_and_never_sets_itself_off()
    {
        var reader = await factory.SignUpAsync("Rule Maker");
        var rule = new RuleRequest("Finished mysteries", RuleTrigger.Finished, new RuleConditions(Tag: "mystery"),
            [new RuleAction(RuleActionKind.AddToCollection, "Solved"), new RuleAction(RuleActionKind.AddTag, "done")]);
        Assert.Equal(HttpStatusCode.Created, (await reader.PostAsJsonAsync("/books/rules", rule, JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/books/rules", rule with { Actions = [] }, JsonOptions)).StatusCode);
        var made = Assert.Single((await reader.GetFromJsonAsync<RuleView[]>("/books/rules", JsonOptions))!);

        var mystery = await CreateAsync(reader, new CreateBookRequest("The Moonstone", "Wilkie Collins", BookStatus.Finished, 4, Tags: ["mystery"]));
        var other = await CreateAsync(reader, new CreateBookRequest("Persuasion", "Jane Austen", BookStatus.Finished, 4));

        // A dry run says what it would do, and does nothing.
        var tried = (await (await reader.PostAsync($"/books/rules/{made.Id}/try", null)).Content.ReadFromJsonAsync<DryRunResult>(JsonOptions))!;
        Assert.Equal(1, tried.Matched);
        Assert.Equal(mystery.Id, Assert.Single(tried.Would).BookId);
        Assert.DoesNotContain("done", (await Book(reader, mystery.Id)).Tags);

        // Started and finished again, the rule acts on the mystery alone.
        var moonstone = await CreateAsync(reader, new CreateBookRequest("The Woman in White", "Wilkie Collins", BookStatus.Reading, null, Tags: ["mystery"]));
        var update = new UpdateBookRequest(moonstone.Title, moonstone.Author, BookStatus.Finished, 5, Tags: ["mystery"], FinishedOn: DateOnly.FromDateTime(DateTime.UtcNow));
        Assert.Equal(HttpStatusCode.OK, (await reader.PutAsJsonAsync($"/books/{moonstone.Id}", update, JsonOptions)).StatusCode);
        await WaitIdleAsync();
        Assert.Contains("done", (await Book(reader, moonstone.Id)).Tags);
        Assert.DoesNotContain("done", (await Book(reader, other.Id)).Tags);
        var run = Assert.Single((await reader.GetFromJsonAsync<RunView[]>("/books/rules/runs", JsonOptions))!);
        Assert.Equal(moonstone.Id, run.BookId);
        Assert.NotNull(run.BatchId);

        // What it did is undone like any change of many books.
        Assert.Equal(HttpStatusCode.OK, (await reader.PostAsync($"/books/changes/{run.BatchId}/undo", null)).StatusCode);
        Assert.DoesNotContain("done", (await Book(reader, moonstone.Id)).Tags);
        Assert.Empty((await (await factory.SignUpAsync("Rule Stranger")).GetFromJsonAsync<RuleView[]>("/books/rules", JsonOptions))!);
    }

    [Fact]
    public async Task Each_device_says_where_a_reader_is_and_the_reader_can_choose_one_even_going_back()
    {
        var reader = await factory.SignUpAsync("Two Devices");
        var book = await CreateAsync(reader, new CreateBookRequest("Long Read", "Someone", BookStatus.Reading, null));
        var readerId = await factory.ReaderIdAsync("Two Devices");
        await using (var scope = Scope(readerId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var open = (await Lending.OpenAsync(db, book.Id))!;
            using (Positions.Reporting("KOReader", "Kobo Clara"))
            {
                await Lending.KeepPlaceAsync(db, open, place => place.EbookChapter = 7);
            }

            // Read offline, behind the Kobo: told, but the place only moves forward by itself.
            using (Positions.Reporting("Shelf", "offline"))
            {
                await Lending.KeepPlaceAsync(db, open, place => place.EbookChapter = place.EbookChapter is int at && at > 4 ? at : 4);
            }
        }

        var where = (await reader.GetFromJsonAsync<PositionsOfBook>($"/books/{book.Id}/positions", JsonOptions))!;
        Assert.Equal(2, where.Reports.Length);
        var shelf = where.Reports.Single(report => report.Source == "Shelf");
        Assert.False(shelf.Current);
        Assert.True(where.Reports.Single(report => report.Source == "KOReader").Current);

        // Chosen, the place goes back to the earlier chapter, and the choice is kept.
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync($"/books/{book.Id}/positions/{shelf.Id}/use", null)).StatusCode);
        Assert.Equal(4, (await reader.GetFromJsonAsync<PlaceResponse>($"/books/{book.Id}/place", JsonOptions))!.EbookChapter);
        var after = (await reader.GetFromJsonAsync<PositionsOfBook>($"/books/{book.Id}/positions", JsonOptions))!;
        Assert.Equal("part 5", Assert.Single(after.Chosen).Place);
        Assert.Contains("Where each device has you", await reader.GetStringAsync($"/library/{book.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await (await factory.SignUpAsync("Position Stranger")).GetAsync($"/books/{book.Id}/positions")).StatusCode);
    }

    [Fact]
    public async Task Notes_reviews_quotes_and_the_journal_are_searched_beside_the_books()
    {
        var reader = await factory.SignUpAsync("Note Searcher");
        var book = await CreateAsync(reader, new CreateBookRequest("Walden", "Henry David Thoreau", BookStatus.Finished, 5, Notes: "Read by the pond in a cabin.", Review: "Simplicity, simplicity."));
        await reader.PostAsJsonAsync($"/books/{book.Id}/quotes", new CreateQuoteRequest("I went to the woods because I wished to live deliberately.", 90), JsonOptions);
        var hits = (await reader.GetFromJsonAsync<NoteHit[]>("/books/notes/search?q=woods", JsonOptions))!;
        Assert.Equal(NoteKind.Quote, Assert.Single(hits).Kind);
        Assert.Equal(NoteKind.Note, Assert.Single((await reader.GetFromJsonAsync<NoteHit[]>("/books/notes/search?q=CABIN", JsonOptions))!).Kind);
        Assert.Empty((await reader.GetFromJsonAsync<NoteHit[]>("/books/notes/search?q=cabin&status=Want", JsonOptions))!);
        Assert.Contains("In your own words", await reader.GetStringAsync("/search?q=simplicity"));
        Assert.Empty((await (await factory.SignUpAsync("Note Stranger")).GetFromJsonAsync<NoteHit[]>("/books/notes/search?q=woods", JsonOptions))!);
    }

    [Fact]
    public async Task Gaps_and_publishers_written_two_ways_are_found_for_tidying()
    {
        var reader = await factory.SignUpAsync("Tidier");
        await CreateAsync(reader, new CreateBookRequest("No Number", "Someone", BookStatus.Want, null, Series: "Saga", Format: BookFormat.Paperback, Isbn: "9780441013593", Publisher: "Penguin Books"));
        await CreateAsync(reader, new CreateBookRequest("No Isbn", "Someone", BookStatus.Want, null, Format: BookFormat.Hardcover, Publisher: "PENGUIN", Pages: 9000));
        await CreateAsync(reader, new CreateBookRequest("Heard", "Someone", BookStatus.Want, null, Format: BookFormat.Audiobook));
        await using var scope = Scope(await factory.ReaderIdAsync("Tidier"));
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var flags = (await Tidy.FlagsAsync(db)).ToDictionary(flag => flag.Kind);
        Assert.Equal("No Isbn", Assert.Single(flags["isbn"].Books).Title);
        Assert.Equal("No Number", Assert.Single(flags["number"].Books).Title);
        Assert.Equal("No Isbn", Assert.Single(flags["pages"].Books).Title);
        var group = Assert.Single(await Tidy.PublisherGroupsAsync(db));
        Assert.Equal(["PENGUIN", "Penguin Books"], group.Spellings.Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, await Tidy.UsePublisherAsync(db, [.. group.Spellings.Select(item => item.Name)], "Penguin Books"));
        Assert.Empty(await Tidy.PublisherGroupsAsync(db));
        Assert.Contains("Gaps and oddities", await reader.GetStringAsync("/tidy"));
    }

    [Fact]
    public async Task A_download_carries_a_manifest_that_shows_it_whole_and_finds_a_changed_file()
    {
        var reader = await factory.SignUpAsync("Archivist");
        await CreateAsync(reader, new CreateBookRequest("Kept Safe", "Someone", BookStatus.Want, null));
        var bytes = await reader.GetByteArrayAsync("/books/export/full");
        var path = Path.Combine(Path.GetTempPath(), $"shelf-manifest-{Guid.NewGuid():N}.zip");
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            using (var zip = ZipFile.OpenRead(path))
            {
                Assert.NotNull(zip.GetEntry(ArchiveManifest.SchemaName));
                var manifest = JsonSerializer.Deserialize<Manifest>(zip.GetEntry(ArchiveManifest.EntryName)!.Open(), Preservation.Json)!;
                Assert.Contains(manifest.Files, file => file.Path == "shelf.json" && file.Sha256.Length == 64);
                Assert.Contains(manifest.Files, file => file.Path == "books.csv");
            }

            var whole = Preservation.Check(path);
            Assert.True(whole.Whole);

            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("books.csv")!.Delete();
                await using var writer = new StreamWriter(zip.CreateEntry("books.csv").Open());
                await writer.WriteAsync("changed");
            }

            Assert.Equal(["books.csv"], Preservation.Check(path).Damaged);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.NotNull(Preservation.Risk("old.mobi"));
        Assert.NotNull(Preservation.Risk("book.AZW3"));
        Assert.Null(Preservation.Risk("book.epub"));
    }

    [Fact]
    public async Task An_admin_sees_the_servers_health_and_what_each_reader_can_see_and_why()
    {
        var admin = factory.Client;
        var health = await admin.GetStringAsync("/admin/health");
        Assert.Contains("Nightly backups", health);
        Assert.Contains("Disk space", health);

        var owner = await factory.SignUpAsync("Open Owner");
        var mine = await CreateAsync(owner, new CreateBookRequest("Open Book", "Someone", BookStatus.Want, null));
        await owner.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(true), JsonOptions);
        await factory.SignUpAsync("Neighbour");
        var neighbour = await factory.ReaderIdAsync("Neighbour");
        await using var scope = Scope(factory.ReaderId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var view = (await AccessPreview.ForReaderAsync(db, neighbour))!;
        Assert.Contains(view.Lines, line => line.Owner == "Open Owner" && line.Count == 1);
        var why = (await AccessPreview.ExplainAsync(db, neighbour, mine.Id))!;
        Assert.True(why.Visible);
        Assert.Contains("Open Owner", Assert.Single(why.Reasons));

        // Not for anyone but an admin.
        Assert.DoesNotContain("Disk space", await owner.GetStringAsync("/admin/health"));
        Assert.DoesNotContain("Why can they see this book?", await owner.GetStringAsync("/admin/access"));
    }

    [Fact]
    public async Task A_question_in_everyday_words_is_answered_from_the_shelf()
    {
        var today = new DateOnly(2026, 10, 14);
        var asked = Concierge.Read("Which unread books do I own about Roman history?", today);
        Assert.Equal((BookStatus.Want, AskLooksFor.Books), (asked.Status!.Value, asked.LooksFor));
        Assert.Equal(["roman", "history"], asked.Words!);
        var friend = Concierge.Read("Find the mystery novel my friend recommended last year.", today);
        Assert.Equal((true, 2025), (friend.Recommended, friend.FromYear!.Value));
        Assert.Equal(["mystery"], friend.Words!);
        var series = Concierge.Read("Which books in the Discworld series have I not read?", today, ["Discworld"]);
        Assert.Equal(("Discworld", true), (series.Series!, series.Unfinished));
        Assert.Equal(AskLooksFor.Quotes, Concierge.Read("Show my favourite quotes about creativity.", today).LooksFor);
        Assert.Equal("Ursula K. Le Guin", Concierge.Read("Which audiobooks by Ursula K. Le Guin have I finished?", today).Author);

        var reader = await factory.SignUpAsync("Asker");
        await CreateAsync(reader, new CreateBookRequest("SPQR", "Mary Beard", BookStatus.Want, null, Tags: ["roman history"]));
        await CreateAsync(reader, new CreateBookRequest("The History of Rome", "Livy", BookStatus.Finished, 4));
        var mort = await CreateAsync(reader, new CreateBookRequest("Mort", "Terry Pratchett", BookStatus.Finished, 5, Series: "Discworld", SeriesNumber: 4));
        await CreateAsync(reader, new CreateBookRequest("Sourcery", "Terry Pratchett", BookStatus.Want, null, Series: "Discworld", SeriesNumber: 5));
        var answer = (await reader.GetFromJsonAsync<AskAnswer>($"/books/ask?q={Uri.EscapeDataString("Which unread books do I own about Roman history?")}", JsonOptions))!;
        Assert.Equal("SPQR", Assert.Single(answer.Books).Title);
        var unread = (await reader.GetFromJsonAsync<AskAnswer>($"/books/ask?q={Uri.EscapeDataString("Which books in the Discworld series have I not read?")}", JsonOptions))!;
        Assert.Equal("Sourcery", Assert.Single(unread.Books).Title);
        Assert.DoesNotContain(unread.Books, book => book.Id == mort.Id);
        Assert.Contains("SPQR", await reader.GetStringAsync($"/ask?q={Uri.EscapeDataString("unread roman history")}"));
    }

    [Fact]
    public async Task A_plan_for_the_library_is_kept_sealed_and_comes_out_as_a_list_of_who_has_what()
    {
        var reader = await factory.SignUpAsync("Planner");
        await CreateAsync(reader, new CreateBookRequest("Signed First", "Someone", BookStatus.Finished, 5, Tags: ["signed"], Signed: true));
        await CreateAsync(reader, new CreateBookRequest("Ordinary", "Someone", BookStatus.Want, null));
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync("/books/legacy", new LegacyPlanRequest("Signed copies to my niece.", "My sister, 555-0100"), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync("/books/legacy/recipients", new LegacyRecipientRequest("Niece Ada", "ada@example.org", "For you.", "signed", null, false), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync("/books/legacy/recipients", new LegacyRecipientRequest("The town library", null, null, null, null, true), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/books/legacy/recipients", new LegacyRecipientRequest("Nobody", null, null, null, null, false), JsonOptions)).StatusCode);

        var view = (await reader.GetFromJsonAsync<LegacyView>("/books/legacy", JsonOptions))!;
        Assert.Equal("Signed copies to my niece.", view.Instructions);
        Assert.Equal([1, 1], view.Recipients.Select(person => person.Books));
        Assert.Equal(0, view.Unassigned);
        var csv = await reader.GetStringAsync("/books/legacy/inventory.csv");
        Assert.Contains("Niece Ada,Signed First", csv);
        Assert.Contains("The town library,Ordinary", csv);
        Assert.Contains("Signed First", await reader.GetStringAsync("/account/legacy/print"));

        // Sealed where it is kept, and no one else's to see.
        await using (var scope = Scope(await factory.ReaderIdAsync("Planner")))
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.DoesNotContain("niece", (await db.LegacyPlans.SingleAsync()).Instructions!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(await db.LegacyRecipients.ToListAsync(), person => person.Name.Contains("Ada", StringComparison.Ordinal));
        }

        Assert.Null((await (await factory.SignUpAsync("Plan Stranger")).GetFromJsonAsync<LegacyView>("/books/legacy", JsonOptions))!.Instructions);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync("/books/legacy")).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<LegacyView>("/books/legacy", JsonOptions))!.Recipients);
    }

    [Fact]
    public async Task An_admin_is_told_of_a_newer_release_until_they_say_not_now()
    {
        var watch = factory.Services.GetRequiredService<ReleaseWatch>();
        Assert.Null(watch.Consider("""{"tag_name":"v0.1.0","html_url":"https://github.com/doodersrage/shelf/releases/tag/v0.1.0"}""", "2.12.0"));
        Assert.Null(watch.Consider("""{"tag_name":"v99.0.0","html_url":"https://example.org/x","prerelease":true}""", "2.12.0"));
        var newer = watch.Consider("""{"tag_name":"v99.0.0","html_url":"https://github.com/doodersrage/shelf/releases/tag/v99.0.0","published_at":"2026-10-14T00:00:00Z"}""", ShelfVersion.Number);
        Assert.Equal("99.0.0", newer!.Version);
        try
        {
            var admin = factory.Client;
            Assert.Contains("Shelf 99.0.0 is out", await admin.GetStringAsync("/library"));
            Assert.DoesNotContain("Shelf 99.0.0 is out", await (await factory.SignUpAsync("Not An Admin")).GetStringAsync("/library"));

            await ShelfApiFactory.PostFormAsync(admin, "/library", "/admin/release/dismiss", []);
            Assert.DoesNotContain("Shelf 99.0.0 is out", await admin.GetStringAsync("/library"));
            Assert.Contains("Shelf 99.0.0 is out", await admin.GetStringAsync("/admin/health"));
        }
        finally
        {
            watch.Consider("""{"tag_name":"v0.0.1","html_url":"https://github.com/doodersrage/shelf"}""", ShelfVersion.Number);
        }
    }

    [Fact]
    public void Lecture_courses_match_however_goodreads_names_them_but_one_volume_never_matches_another()
    {
        var shelf = new ShelfMatcher();
        shelf.Add(1, "Heavens Above: Stars, Constellations, and the Sky", "James B. Kaler", null);
        shelf.Add(2, "Ethics: A History of Moral Thought", "Peter Kreeft", null);
        shelf.Add(3, "Odyssey of the West III: The Medieval World", "Timothy B. Shutt", null);
        shelf.Add(4, "The Sisters", "Aoife McMahon", null);
        shelf.Add(5, "The Norsemen: Understanding Vikings and Their Culture", "Michael D. C. Drout", null);
        shelf.Add(6, "The Iliad", "Homer", null);

        // The series in front, the series as author, a narrator named among the additional authors, initials spaced.
        Assert.Equal([1], shelf.Find("The Modern Scholar: Heavens Above: Stars, Constellations, and the Sky", "James B. Kaler", "9781428185715"));
        Assert.Equal([2], shelf.Find("Ethics: A History of Moral Thought", "The Modern Scholar", "9781402547706"));
        Assert.Equal([4], shelf.Find("The Sisters", "Dervla McTiernan", null, ["Aoife McMahon"]));
        Assert.Equal([5], shelf.Find("The Norsemen: Understanding Vikings and Their Culture (The Modern Scholar)", "Michael D.C. Drout", null));

        // Another volume of a course, a short title by someone else, and a retelling are other books.
        Assert.Empty(shelf.Find("Odyssey of the West I: Hebrews and Greeks", "Timothy B. Shutt", "9781428127449"));
        Assert.Empty(shelf.Find("The Sisters", "Someone Else", null));
        Assert.Empty(shelf.Find("The Iliad", "Pauline Francis", "9781783220588"));
        Assert.Equal([6], shelf.Find("The Iliad", "Pauline Francis", "9781783220588", ["Homer"]));
    }

    private async Task<BookResponse> Book(HttpClient client, int id) => (await client.GetFromJsonAsync<BookResponse>($"/books/{id}", JsonOptions))!;

    private static async Task<BookResponse> CreateAsync(HttpClient client, CreateBookRequest request)
    {
        var response = await client.PostAsJsonAsync("/books", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
    }

    private AsyncServiceScope Scope(int readerId)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        return scope;
    }

    private async Task WaitIdleAsync()
    {
        var events = factory.Services.GetRequiredService<ShelfEvents>();
        for (var i = 0; i < 200 && !events.Idle; i++)
        {
            await Task.Delay(50);
        }
    }

    // A code the catalogs couldn't be asked about is counted apart from one they don't know, and stays on the list.
    [Fact]
    public async Task A_scan_tells_catalogs_out_of_reach_from_a_book_they_dont_know()
    {
        var reader = await factory.SignUpAsync("Scanning In Outage");
        var readerId = await factory.ReaderIdAsync("Scanning In Outage");
        var run = (await (await reader.PostAsJsonAsync("/books/scans", new Scanning.ScanStart(ScanKind.Add, "Hall"), JsonOptions)).Content.ReadFromJsonAsync<ScanSessionView>(JsonOptions))!;
        await reader.PostAsJsonAsync($"/books/scans/{run.Id}/codes", new Scanning.ScanCode("9780441013593"), JsonOptions);

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var added = (await Scanning.AddAsync(db, new UnreachableLookup(), run.Id, new ScanAddRequest(["9780441013593"], null, BookStatus.Want)))!;
        Assert.Equal((0, 0, 1), (added.Added, added.Skipped, added.Unreachable));
        Assert.Contains("9780441013593", (await db.ScanSessions.AsNoTracking().FirstAsync(item => item.Id == run.Id)).Codes);
    }

    private sealed class UnreachableLookup : IBookLookup
    {
        public bool CouldNotAsk => true;

        public Task<CatalogMatch?> FindAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken) => Task.FromResult<CatalogMatch?>(null);
    }

    // A box's destination is one of the reader's own places: naming another reader's would block them removing it.
    [Fact]
    public async Task A_destination_is_one_of_your_own_places()
    {
        var other = await factory.SignUpAsync("Other Keeper");
        var theirs = (await (await other.PostAsJsonAsync("/books/storage", new StoragePlaceRequest("Their room"), JsonOptions)).Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).GetProperty("id").GetInt32();
        var reader = await factory.SignUpAsync("Box Packer");
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/books/storage", new StoragePlaceRequest("Box", PlaceKind.Box, DestinationId: theirs), JsonOptions)).StatusCode);
        var mine = (await (await reader.PostAsJsonAsync("/books/storage", new StoragePlaceRequest("My room"), JsonOptions)).Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).GetProperty("id").GetInt32();
        var box = (await (await reader.PostAsJsonAsync("/books/storage", new StoragePlaceRequest("Box", PlaceKind.Box, DestinationId: mine), JsonOptions)).Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PutAsJsonAsync($"/books/storage/{box}", new StoragePlaceRequest("Box", PlaceKind.Box, DestinationId: theirs), JsonOptions)).StatusCode);
        Assert.True((await other.DeleteAsync($"/books/storage/{theirs}")).IsSuccessStatusCode);
    }
}
