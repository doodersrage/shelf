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

// The library's time machine and its care: the trash, the history of changes and undoing them, loans kept after they
// end, scanning a shelf, files checked, details looked over before they change, near duplicates, and backups proved.
public sealed class TimeMachineTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_deleted_book_waits_in_the_trash_and_comes_back_whole_with_its_id()
    {
        var reader = await factory.SignUpAsync("Trash Reader");
        var book = await CreateAsync(reader, new CreateBookRequest("The Dispossessed", "Ursula K. Le Guin", BookStatus.Finished, 5, Tags: ["anarchy", "moons"]));
        var quote = await (await reader.PostAsJsonAsync($"/books/{book.Id}/quotes", new CreateQuoteRequest("True journey is return.", 386), JsonOptions)).Content.ReadFromJsonAsync<QuoteResponse>(JsonOptions);
        await reader.PostAsJsonAsync($"/books/{book.Id}/sessions", new CreateSessionRequest(new DateOnly(2024, 4, 1), 1, 90, "Anarres."), JsonOptions);

        // A quote alone comes back to its book.
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/{book.Id}/quotes/{quote!.Id}")).StatusCode);
        var trashed = Assert.Single((await reader.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions))!);
        Assert.Equal((TrashKind.Quote, "True journey is return.", 30), (trashed.Kind, trashed.Title, trashed.DaysLeft));
        Assert.Equal(HttpStatusCode.OK, (await reader.PostAsync($"/books/trash/{trashed.Id}/restore", null)).StatusCode);
        Assert.Single((await reader.GetFromJsonAsync<QuoteResponse[]>($"/books/{book.Id}/quotes", JsonOptions))!);

        // The whole book, then.
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/{book.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/books/{book.Id}")).StatusCode);
        var item = Assert.Single((await reader.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions))!);
        Assert.Equal((TrashKind.Book, "The Dispossessed", "Ursula K. Le Guin"), (item.Kind, item.Title, item.Detail));

        // Nobody else sees it, or can bring it back.
        var other = await factory.SignUpAsync("Trash Bystander");
        Assert.Empty((await other.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/books/trash/{item.Id}/restore", null)).StatusCode);

        var restored = await (await reader.PostAsync($"/books/trash/{item.Id}/restore", null)).Content.ReadFromJsonAsync<TrashRestore>(JsonOptions);
        Assert.Equal((true, (int?)book.Id), (restored!.Restored, restored.BookId));
        var back = (await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!;
        Assert.Equal(["anarchy", "moons"], back.Tags.Order());
        Assert.Single(back.Quotes);
        Assert.Single(back.Sessions);
        Assert.Empty((await reader.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions))!);

        // Emptying the trash lets things go for good.
        await reader.DeleteAsync($"/books/{book.Id}");
        Assert.Equal(HttpStatusCode.OK, (await reader.DeleteAsync("/books/trash")).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions))!);
    }

    [Fact]
    public async Task A_bulk_edit_and_an_import_are_undone_at_once_but_a_later_edit_by_hand_stays()
    {
        var reader = await factory.SignUpAsync("Undo Reader");
        var first = await CreateAsync(reader, new CreateBookRequest("Kindred", "Octavia E. Butler", BookStatus.Want, null));
        var second = await CreateAsync(reader, new CreateBookRequest("Dawn", "Octavia E. Butler", BookStatus.Want, null));

        var bulk = await reader.PostAsJsonAsync("/books/bulk", new BulkRequest([first.Id, second.Id], BookStatus.Reading, AddTag: "butler"), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);

        // The second is changed again by hand: undoing leaves its status as the reader set it.
        var update = BookWrite.From((await reader.GetFromJsonAsync<BookResponse>($"/books/{second.Id}", JsonOptions))!) with { Status = BookStatus.Finished, Rating = 4 };
        Assert.Equal(HttpStatusCode.OK, (await reader.PutAsJsonAsync($"/books/{second.Id}", ToUpdate(update), JsonOptions)).StatusCode);

        var batch = (await reader.GetFromJsonAsync<BatchItem[]>("/books/changes", JsonOptions))!.First();
        Assert.Equal((BatchKind.Edit, 2, true), (batch.Kind, batch.Books, batch.CanUndo));
        Assert.Contains("butler", batch.Summary);
        Assert.Equal(HttpStatusCode.OK, (await reader.PostAsync($"/books/changes/{batch.Id}/undo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.PostAsync($"/books/changes/{batch.Id}/undo", null)).StatusCode);

        var one = (await reader.GetFromJsonAsync<BookResponse>($"/books/{first.Id}", JsonOptions))!;
        var two = (await reader.GetFromJsonAsync<BookResponse>($"/books/{second.Id}", JsonOptions))!;
        Assert.Equal((BookStatus.Want, 0), (one.Status, one.Tags.Length));
        Assert.Equal((BookStatus.Finished, 0), (two.Status, two.Tags.Length));

        // A book's own history lists each change, and any one can be put back.
        var history = (await reader.GetFromJsonAsync<BookChangeItem[]>($"/books/{second.Id}/history", JsonOptions))!;
        var finishing = history.First(change => change.Field == nameof(Book.Status) && change.After == BookRules.StatusLabel(BookStatus.Finished));
        Assert.Equal(BookRules.StatusLabel(BookStatus.Reading), finishing.Before);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync($"/books/{second.Id}/history/{finishing.Id}/undo", null)).StatusCode);
        Assert.Equal(BookStatus.Reading, (await reader.GetFromJsonAsync<BookResponse>($"/books/{second.Id}", JsonOptions))!.Status);

        // An import, undone, sends what it brought to the trash.
        var source = await factory.SignUpAsync("Undo Source");
        await CreateAsync(source, new CreateBookRequest("The Parable of the Sower", "Octavia E. Butler", BookStatus.Want, null));
        await CreateAsync(source, new CreateBookRequest("Wild Seed", "Octavia E. Butler", BookStatus.Want, null));
        var export = (await source.GetFromJsonAsync<LibraryExport>("/books/export", JsonOptions))!;
        Assert.Equal(HttpStatusCode.OK, (await reader.PostAsJsonAsync("/books/import", export, JsonOptions)).StatusCode);
        var import = (await reader.GetFromJsonAsync<BatchItem[]>("/books/changes", JsonOptions))!.First(item => item.Kind == BatchKind.Import);
        Assert.Equal(2, import.Books);
        Assert.Equal(2, (await (await reader.PostAsync($"/books/changes/{import.Id}/undo", null)).Content.ReadFromJsonAsync<UndoResult>(JsonOptions))!.Books);
        var titles = (await reader.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!.Select(book => book.Title).ToList();
        Assert.DoesNotContain("Wild Seed", titles);
        Assert.Equal(2, (await reader.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions))!.Count(item => item.Kind == TrashKind.Book));

        // Books deleted together come back together.
        await reader.PostAsJsonAsync("/books/bulk", new BulkRequest([first.Id, second.Id], Delete: true), JsonOptions);
        Assert.Empty((await reader.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!);
        var deleted = (await reader.GetFromJsonAsync<BatchItem[]>("/books/changes", JsonOptions))!.First(item => item.Kind == BatchKind.Delete);
        await reader.PostAsync($"/books/changes/{deleted.Id}/undo", null);
        Assert.Equal(2, (await reader.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!.Length);

        // Only one's own changes are listed.
        var other = await factory.SignUpAsync("Undo Bystander");
        Assert.Empty((await other.GetFromJsonAsync<BatchItem[]>("/books/changes", JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/books/changes/{deleted.Id}/undo", null)).StatusCode);
    }

    [Fact]
    public async Task A_merge_sends_the_other_entry_to_the_trash()
    {
        var reader = await factory.SignUpAsync("Merge Trash Reader");
        var keep = await CreateAsync(reader, new CreateBookRequest("Beloved", "Toni Morrison", BookStatus.Finished, 5));
        var other = await CreateAsync(reader, new CreateBookRequest("Beloved: A Novel", "Toni Morrison", BookStatus.Want, null, Year: 1987));
        Assert.True((await reader.PostAsJsonAsync($"/books/{keep.Id}/merge", new MergeRequest(other.Id), JsonOptions)).IsSuccessStatusCode);
        Assert.Equal(1987, (await reader.GetFromJsonAsync<BookResponse>($"/books/{keep.Id}", JsonOptions))!.Year);
        Assert.Equal("Beloved: A Novel", Assert.Single((await reader.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions))!).Title);
    }

    [Fact]
    public async Task The_trash_lets_go_after_its_days_and_old_history_is_trimmed()
    {
        var reader = await factory.SignUpAsync("Purge Reader");
        var book = await CreateAsync(reader, new CreateBookRequest("Old News", "A. Writer", BookStatus.Want, null));
        await reader.DeleteAsync($"/books/{book.Id}");
        var fresh = await CreateAsync(reader, new CreateBookRequest("New News", "A. Writer", BookStatus.Want, null));
        await reader.DeleteAsync($"/books/{fresh.Id}");

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Purge Reader"));
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        // As it would stand forty days on: what was put in before it, any reader's, is as old.
        var old = await db.TrashItems.FirstAsync(item => item.Title == "Old News");
        foreach (var earlier in await db.TrashItems.IgnoreQueryFilters().Where(item => item.Id <= old.Id).ToListAsync())
        {
            earlier.DeletedAt = DateTimeOffset.UtcNow.AddDays(-40);
        }

        await db.SaveChangesAsync();

        // Only items put in before the one young enough are looked at, as the trash keeps them in order.
        var gone = await Trash.PurgeAsync(db, scope.ServiceProvider.GetRequiredService<EbookStore>(), scope.ServiceProvider.GetRequiredService<AudioStore>(),
            scope.ServiceProvider.GetRequiredService<CoverStore>(), 30);
        Assert.True(gone >= 1);
        Assert.Equal(["New News"], (await reader.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions))!.Select(item => item.Title));
        Assert.True(await History.TrimAsync(db, 1) >= 0);
    }

    [Fact]
    public async Task A_loan_is_kept_after_it_ends_with_its_renewals_and_the_state_it_came_back_in()
    {
        var reader = await factory.SignUpAsync("Lending Librarian");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var book = await CreateAsync(reader, new CreateBookRequest("Gilead", "Marilynne Robinson", BookStatus.Finished, 5,
            LoanedTo: "Ged", LoanedOn: today.AddDays(-10), DueOn: today.AddDays(4), Condition: CopyCondition.Fine));

        // Renewed for as long again as it first ran.
        var renewed = await reader.PostAsJsonAsync($"/books/{book.Id}/renew", new RenewRequest(), JsonOptions);
        Assert.Equal(today.AddDays(18), await renewed.Content.ReadFromJsonAsync<DateOnly>(JsonOptions));

        var back = await reader.PostAsJsonAsync($"/books/{book.Id}/return", new ReturnRequest(CopyCondition.Good, "Coffee ring on the cover."), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(CopyCondition.Good, (await back.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!.Condition);

        var loan = Assert.Single((await reader.GetFromJsonAsync<LoanRecordItem[]>($"/books/{book.Id}/loans", JsonOptions))!);
        Assert.Equal(("Ged", today.AddDays(-10), (DateOnly?)today, 1, (CopyCondition?)CopyCondition.Fine, (CopyCondition?)CopyCondition.Good, "Coffee ring on the cover."),
            (loan.Borrower, loan.LentOn, loan.ReturnedOn, loan.Renewals, loan.ConditionOut, loan.ConditionBack, loan.Note));
        Assert.Contains((await reader.GetFromJsonAsync<LoanRecordItem[]>("/books/loans/history", JsonOptions))!, item => item.BookId == book.Id);

        // Lent again by editing the book: a second loan opens.
        var update = BookWrite.From((await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!) with { LoanedTo = "Tenar", LoanedOn = today };
        await reader.PutAsJsonAsync($"/books/{book.Id}", ToUpdate(update), JsonOptions);
        var loans = (await reader.GetFromJsonAsync<LoanRecordItem[]>($"/books/{book.Id}/loans", JsonOptions))!;
        Assert.Equal(["Tenar", "Ged"], loans.Select(item => item.Borrower));
        Assert.Null(loans[0].ReturnedOn);

        // Out of the trash, its loans come back as they were, the open one still open, and no new one is opened.
        await reader.DeleteAsync($"/books/{book.Id}");
        var trashed = Assert.Single((await reader.GetFromJsonAsync<TrashView[]>("/books/trash", JsonOptions))!);
        await reader.PostAsync($"/books/trash/{trashed.Id}/restore", null);
        Assert.Equal(loans.Select(item => (item.Borrower, item.ReturnedOn)), (await reader.GetFromJsonAsync<LoanRecordItem[]>($"/books/{book.Id}/loans", JsonOptions))!.Select(item => (item.Borrower, item.ReturnedOn)));
        Assert.Equal(HttpStatusCode.NotFound, (await (await factory.SignUpAsync("Loan Bystander")).GetAsync($"/books/{book.Id}/loans")).StatusCode);
    }

    [Fact]
    public async Task A_shelf_is_checked_by_scanning_and_what_was_not_seen_is_tagged_missing_until_undone()
    {
        var reader = await factory.SignUpAsync("Shelf Checker");
        var here = await CreateAsync(reader, new CreateBookRequest("Here", "A. Author", BookStatus.Want, null, Isbn: "9780000000002", Location: "Study"));
        var lost = await CreateAsync(reader, new CreateBookRequest("Lost", "A. Author", BookStatus.Want, null, Isbn: "9780000000019", Location: "Study"));
        var hall = await CreateAsync(reader, new CreateBookRequest("Hall", "A. Author", BookStatus.Want, null, Isbn: "9780000000026", Location: "Hall"));

        Assert.Equal(["Hall", "Study"], (await reader.GetFromJsonAsync<string[]>("/books/scans/places", JsonOptions))!);
        var session = (await (await reader.PostAsJsonAsync("/books/scans", new Scanning.ScanStart(ScanKind.Check, "Study"), JsonOptions)).Content.ReadFromJsonAsync<ScanSessionView>(JsonOptions))!;
        async Task<ScanCheck> Read(string code) => (await (await reader.PostAsJsonAsync($"/books/scans/{session.Id}/codes", new Scanning.ScanCode(code), JsonOptions)).Content.ReadFromJsonAsync<ScanCheck>(JsonOptions))!;
        Assert.Equal((ScanFinding.Here, (int?)here.Id), ((await Read("978-0-00-000000-2")).Finding, (await Read("9780000000002")).BookId));
        Assert.Equal(ScanFinding.Again, (await Read("9780000000002")).Finding);
        Assert.Equal((ScanFinding.Elsewhere, "Hall"), ((await Read("9780000000026")).Finding, (await Read("9780000000026")).Location));
        Assert.Equal(ScanFinding.Unknown, (await Read("9780306406157")).Finding);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.PostAsJsonAsync($"/books/scans/{session.Id}/codes", new Scanning.ScanCode("not a code"), JsonOptions)).StatusCode);

        // Paused and taken up on another device: the run is kept.
        var open = Assert.Single((await reader.GetFromJsonAsync<ScanSessionView[]>("/books/scans", JsonOptions))!);
        Assert.Equal([here.Id], open.Seen);
        var expected = (await reader.GetFromJsonAsync<CheckedBook[]>($"/books/scans/{session.Id}/books", JsonOptions))!;
        Assert.Equal([(lost.Id, false), (here.Id, true)], expected.Select(book => (book.Id, book.Seen)));

        var finished = (await (await reader.PostAsJsonAsync($"/books/scans/{session.Id}/finish", new ScanFinishRequest(true), JsonOptions)).Content.ReadFromJsonAsync<ScanFinished>(JsonOptions))!;
        Assert.Equal((1, 1), (finished.Seen, finished.Missing));
        Assert.Contains(Scanning.MissingTag, (await reader.GetFromJsonAsync<BookResponse>($"/books/{lost.Id}", JsonOptions))!.Tags);
        Assert.Empty((await reader.GetFromJsonAsync<BookResponse>($"/books/{hall.Id}", JsonOptions))!.Tags);
        await reader.PostAsync($"/books/changes/{finished.BatchId}/undo", null);
        Assert.Empty((await reader.GetFromJsonAsync<BookResponse>($"/books/{lost.Id}", JsonOptions))!.Tags);
        Assert.Empty((await reader.GetFromJsonAsync<ScanSessionView[]>("/books/scans", JsonOptions))!);
    }

    [Fact]
    public async Task Books_scanned_one_after_another_are_added_together_and_an_unknown_code_waits()
    {
        var reader = await factory.SignUpAsync("Scanning Adder");
        var session = (await (await reader.PostAsJsonAsync("/books/scans", new Scanning.ScanStart(ScanKind.Add, "Attic"), JsonOptions)).Content.ReadFromJsonAsync<ScanSessionView>(JsonOptions))!;
        await reader.PostAsJsonAsync($"/books/scans/{session.Id}/codes", new Scanning.ScanCode("0441478123"), JsonOptions);
        await reader.PostAsJsonAsync($"/books/scans/{session.Id}/codes", new Scanning.ScanCode("9780441478125"), JsonOptions);
        await reader.PostAsJsonAsync($"/books/scans/{session.Id}/codes", new Scanning.ScanCode("9780306406157"), JsonOptions);
        Assert.Equal(["9780441478125", "9780306406157"], Assert.Single((await reader.GetFromJsonAsync<ScanSessionView[]>("/books/scans", JsonOptions))!).Codes);

        var added = (await (await reader.PostAsJsonAsync($"/books/scans/{session.Id}/add", new ScanAddRequest(["9780441478125", "9780306406157"], null), JsonOptions)).Content.ReadFromJsonAsync<ScanAdded>(JsonOptions))!;
        Assert.Equal((1, 1), (added.Added, added.Skipped));
        var book = Assert.Single((await reader.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!);
        Assert.Equal(("The Left Hand of Darkness", "Attic"), (book.Title, book.Location));
        Assert.Equal(["9780306406157"], Assert.Single((await reader.GetFromJsonAsync<ScanSessionView[]>("/books/scans", JsonOptions))!).Codes);
        Assert.Equal(BatchKind.Scan, (await reader.GetFromJsonAsync<BatchItem[]>("/books/changes", JsonOptions))!.Single(item => item.Id == added.BatchId).Kind);
    }

    [Fact]
    public async Task The_file_check_finds_a_file_gone_or_changed_and_says_whose_book_it_is()
    {
        var reader = await factory.SignUpAsync("File Keeper");
        var book = await CreateAsync(reader, new CreateBookRequest("A Kept File", "A. Author", BookStatus.Want, null));
        var other = await CreateAsync(reader, new CreateBookRequest("A Lost File", "A. Author", BookStatus.Want, null));
        foreach (var (id, words) in new[] { (book.Id, "The first file."), (other.Id, "The second file.") })
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(BooksEndpointTests.SampleEpub(words)), "file", "file.epub");
            (await reader.PostAsync($"/books/{id}/ebook", content)).EnsureSuccessStatusCode();
        }

        var ebooks = factory.Services.GetRequiredService<EbookStore>();
        async Task<FileCheckResult> RunAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await FileCheck.RunAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), ebooks, factory.Services.GetRequiredService<AudioStore>(), factory.Services.GetRequiredService<CoverStore>());
        }

        Assert.Equal((0, 0), ((await RunAsync()).Missing, (await RunAsync()).Changed));
        var names = new List<string>();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            names = await db.Books.IgnoreQueryFilters().Where(item => item.Id == book.Id || item.Id == other.Id).OrderBy(item => item.Id).Select(item => item.EbookStoredName!).ToListAsync();
        }

        await File.AppendAllTextAsync(ebooks.OpenPath(names[0])!, "damage");
        File.Delete(ebooks.OpenPath(names[1])!);
        var result = await RunAsync();
        Assert.True(result is { Missing: >= 1, Changed: >= 1 });

        var health = (await reader.GetFromJsonAsync<FileHealth>("/books/files/health", JsonOptions))!;
        Assert.Equal([(book.Id, FileProblem.Changed), (other.Id, FileProblem.Missing)], health.Problems.OrderBy(issue => issue.BookId).Select(issue => (issue.BookId, issue.Problem)));
        Assert.Empty((await (await factory.SignUpAsync("File Bystander")).GetFromJsonAsync<FileHealth>("/books/files/health", JsonOptions))!.Problems);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/books/files/check", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await factory.Client.PostAsync("/books/files/check", null)).StatusCode);
    }

    [Fact]
    public async Task Details_from_the_catalogs_are_looked_over_before_they_change_and_the_change_can_be_undone()
    {
        var reader = await factory.SignUpAsync("Detail Reviewer");
        var book = await CreateAsync(reader, new CreateBookRequest("Piranesi", "Susanna Clarke", BookStatus.Want, null, Isbn: "9781635575637", Year: 2020));
        factory.Sources.Answers["9781635575637"] = (
            new CatalogMatch("Piranesi", "Susanna Clarke", 2020, 272, "Bloomsbury", "English", null),
            new CatalogMatch("Piranesi", "Susanna Clarke", 2020, 245, "Bloomsbury Publishing", "en", null));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Detail Reviewer"));
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var job = (await factory.Services.GetRequiredService<DetailsFiller>().StartAsync(db, DetailsKind.Review))!;
            for (var attempt = 0; attempt < 200 && !job.Finished; attempt++)
            {
                await Task.Delay(50);
            }

            Assert.True(job.Finished);
        }

        // Nothing changed yet; the year both agree with is not proposed.
        Assert.Null((await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!.Pages);
        var proposed = Assert.Single((await reader.GetFromJsonAsync<ProposedBook[]>("/books/details/proposals", JsonOptions))!);
        Assert.Equal(["Pages", "Publisher", "Language"], proposed.Changes.Select(change => change.Field));
        var pages = proposed.Changes.Single(change => change.Field == "Pages");
        Assert.Equal(("272", "245", true), (pages.OpenLibrary, pages.Google, pages.Disagree));

        var applied = (await (await reader.PostAsJsonAsync("/books/details/proposals/apply", new ApplyProposalsRequest([new ProposalChoice(pages.Id, DetailSource.Google)]), JsonOptions))
            .Content.ReadFromJsonAsync<AppliedProposals>(JsonOptions))!;
        Assert.Equal(1, applied.Changed);
        Assert.Equal(245, (await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!.Pages);
        Assert.Equal(2, Assert.Single((await reader.GetFromJsonAsync<ProposedBook[]>("/books/details/proposals", JsonOptions))!).Changes.Length);

        await reader.PostAsync($"/books/changes/{applied.BatchId}/undo", null);
        Assert.Null((await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!.Pages);
        Assert.Empty((await (await factory.SignUpAsync("Detail Bystander")).GetFromJsonAsync<ProposedBook[]>("/books/details/proposals", JsonOptions))!);
    }

    [Fact]
    public async Task Near_duplicates_are_scored_and_two_said_to_differ_are_not_shown_again()
    {
        var reader = await factory.SignUpAsync("Duplicate Reviewer");
        var one = await CreateAsync(reader, new CreateBookRequest("The Name of the Rose", "Umberto Eco", BookStatus.Want, null));
        var two = await CreateAsync(reader, new CreateBookRequest("Name of the Rose (Illustrated Edition)", "Eco, Umberto", BookStatus.Want, null));
        await CreateAsync(reader, new CreateBookRequest("Foucault's Pendulum", "Umberto Eco", BookStatus.Want, null));

        var group = Assert.Single((await reader.GetFromJsonAsync<DuplicateGroup[]>("/books/duplicates", JsonOptions))!);
        Assert.Equal(("similar", 85), (group.Reason, group.Score));
        Assert.Equal([one.Id, two.Id], group.Books.Select(book => book.Id).Order());

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync("/books/duplicates/dismiss", new NotDuplicateRequest([one.Id, two.Id]), JsonOptions)).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<DuplicateGroup[]>("/books/duplicates", JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await (await factory.SignUpAsync("Duplicate Bystander")).PostAsJsonAsync("/books/duplicates/dismiss", new NotDuplicateRequest([one.Id, two.Id]), JsonOptions)).StatusCode);
    }

    [Fact]
    public async Task A_backup_is_proved_by_a_drill_and_one_book_comes_back_from_it()
    {
        if (ShelfApiFactory.OnPostgres)
        {
            return;
        }

        var reader = await factory.SignUpAsync("Backup Restorer");
        var book = await CreateAsync(reader, new CreateBookRequest("The Overstory", "Richard Powers", BookStatus.Finished, 5, Tags: ["trees"]));
        await reader.PostAsJsonAsync($"/books/{book.Id}/quotes", new CreateQuoteRequest("The best arguments in the world won't change a person's mind.", null), JsonOptions);
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n% The Overstory, another format\n%%EOF\n");
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(pdf), "file", "overstory.pdf" } })
        {
            await reader.PostAsync($"/books/{book.Id}/ebook/formats", content);
        }

        var taken = (await (await factory.Client.PostAsync("/admin/backups", null)).Content.ReadFromJsonAsync<AutomaticBackup>(JsonOptions))!;
        var listed = (await factory.Client.GetFromJsonAsync<BackupWithVerdict[]>("/admin/backups", JsonOptions))!.Single(item => item.Name == taken.Name);
        Assert.True(listed.Verdict is { Ok: true, Books: >= 1, Readers: >= 2 }, listed.Verdict?.Problem);

        // Gone from the shelf and from the trash both.
        await reader.DeleteAsync($"/books/{book.Id}");
        await reader.DeleteAsync("/books/trash");
        var gone = Assert.Single((await reader.GetFromJsonAsync<BackupBook[]>($"/books/backups/{taken.Name}/books", JsonOptions))!);
        Assert.Equal("The Overstory", gone.Title);
        Assert.Empty((await (await factory.SignUpAsync("Backup Bystander 2")).GetFromJsonAsync<BackupBook[]>($"/books/backups/{taken.Name}/books", JsonOptions))!);

        Assert.Equal(HttpStatusCode.OK, (await reader.PostAsync($"/books/backups/{taken.Name}/books/{book.Id}/restore", null)).StatusCode);
        var back = (await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!;
        Assert.Equal(["trees"], back.Tags);
        Assert.Single(back.Quotes);
        // Its other format's file is in neither the backup (which leaves files out unless asked) nor the shelf (the
        // trash let it go), so no record comes back pointing at nothing.
        Assert.Empty((await reader.GetFromJsonAsync<EbookFormatResponse[]>($"/books/{book.Id}/ebook/formats", JsonOptions))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsync($"/books/backups/{taken.Name}/books/{book.Id}/restore", null)).StatusCode);

        // A damaged backup fails its drill.
        var path = factory.Services.GetRequiredService<BackupSchedule>().PathOf(taken.Name)!;
        var broken = Path.Combine(Path.GetDirectoryName(path)!, BackupSchedule.Prefix + "broken.zip");
        // Cut off halfway, as a copy that stopped would be: a changed byte can land where the drill never reads.
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(broken, bytes[..(bytes.Length / 2)]);
        var verdict = (await (await factory.Client.PostAsync($"/admin/backups/{Path.GetFileName(broken)}/drill", null)).Content.ReadFromJsonAsync<BackupVerdict>(JsonOptions))!;
        Assert.False(verdict.Ok);
        File.Delete(broken);
    }

    [Fact]
    public async Task A_readers_download_carries_their_own_records_in_plain_formats()
    {
        var reader = await factory.SignUpAsync("Portable Reader");
        var book = await CreateAsync(reader, new CreateBookRequest("=SUM(1,2)", "Formula, A.", BookStatus.Finished, 4, Tags: ["spreadsheets"], LoanedTo: "Lyra"));
        await (await factory.SignUpAsync("Portable Bystander")).PostAsJsonAsync("/books", new CreateBookRequest("Not Mine", "Someone Else", BookStatus.Want, null), JsonOptions);

        using var zip = new ZipArchive(new MemoryStream(await reader.GetByteArrayAsync("/books/export/full")));
        Assert.Contains("README.md", zip.Entries.Select(entry => entry.FullName));
        var csv = await new StreamReader(zip.GetEntry("books.csv")!.Open()).ReadToEndAsync();
        Assert.StartsWith("Number,Title,Subtitle,Author,", csv);
        Assert.Contains("1,'=SUM(1,2)", csv.Replace("\"", ""));
        Assert.DoesNotContain("Not Mine", csv);
        var loans = await new StreamReader(zip.GetEntry("loans.json")!.Open()).ReadToEndAsync();
        Assert.Contains("Lyra", loans);
        Assert.NotNull(zip.GetEntry("collections.json"));
        Assert.NotNull(zip.GetEntry("borrowed-highlights.json"));
        Assert.Equal(book.Id, (await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!.Id);
    }

    private static async Task<BookResponse> CreateAsync(HttpClient client, CreateBookRequest request)
    {
        var response = await client.PostAsJsonAsync("/books", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
    }

    private static UpdateBookRequest ToUpdate(BookWrite write) => new(
        write.Title, write.Author, write.Status, write.Rating, write.Year, write.Isbn, write.Pages, write.CurrentPage, write.Notes, write.StartedOn, write.FinishedOn,
        write.LoanedTo, [.. write.Tags], write.Subtitle, write.Publisher, write.Language, write.Format, write.Series, write.SeriesNumber, write.CoverUrl, write.Review,
        write.Loved, write.LoanedOn, write.Location, write.AcquiredOn, write.Translator, write.OriginalTitle, write.RecommendedBy, write.Inscription, write.DueOn,
        write.Queued, write.Acquisition, write.Condition, write.Narrator, write.PreviousOwners, write.Signed);
}
