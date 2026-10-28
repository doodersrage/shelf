using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

// Admins, removing readers, asking to borrow, reminders, and backups.
public sealed class ShelfCareTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _admin = factory.Client;

    [Fact]
    public async Task The_first_reader_is_the_admin_and_others_are_not()
    {
        var other = await factory.SignUpAsync("Vetch");

        var readers = await _admin.GetFromJsonAsync<ReaderSummary[]>("/admin/readers", JsonOptions);
        Assert.True(readers!.Single(reader => reader.Id == factory.ReaderId).IsAdmin);
        Assert.False(readers!.Single(reader => reader.Name == "Vetch").IsAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/admin/readers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/admin/snapshot")).StatusCode);
        Assert.Contains("Only an admin", await other.GetStringAsync("/admin"));
        Assert.Contains("Download a snapshot", await _admin.GetStringAsync("/admin?tab=tab-backups"));
        Assert.DoesNotContain("href=\"/admin\"", await other.GetStringAsync("/"));
    }

    [Fact]
    public async Task A_reset_password_signs_the_reader_out_and_the_new_one_works()
    {
        var forgetful = await factory.SignUpAsync("Jasper");
        var id = await factory.ReaderIdAsync("Jasper");
        Assert.Equal(HttpStatusCode.OK, (await forgetful.GetAsync("/books")).StatusCode);

        var reset = await _admin.PostAsync($"/admin/readers/{id}/password", null);
        var password = (await reset.Content.ReadFromJsonAsync<TemporaryPassword>(JsonOptions))!.Password;
        Assert.Matches("^[a-z2-9]{4}-[a-z2-9]{4}-[a-z2-9]{4}-[a-z2-9]{4}$", password);

        Assert.Equal(HttpStatusCode.Unauthorized, (await forgetful.GetAsync("/books")).StatusCode);

        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var old = await ShelfApiFactory.PostFormAsync(client, "/signin", "/account/signin", new()
        {
            ["name"] = "Jasper",
            ["password"] = ShelfApiFactory.Password,
        });
        Assert.Contains("problem=WrongPassword", old.Headers.Location?.OriginalString);
        var fresh = await ShelfApiFactory.PostFormAsync(client, "/signin", "/account/signin", new()
        {
            ["name"] = "Jasper",
            ["password"] = password,
        });
        Assert.Equal("/", fresh.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/books")).StatusCode);
    }

    [Fact]
    public async Task Changing_a_password_ends_the_other_sessions()
    {
        var phone = await factory.SignUpAsync("Ivory");
        var id = await factory.ReaderIdAsync("Ivory");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.Null(await ReaderRules.ChangePasswordAsync(db, id, ShelfApiFactory.Password, "a different long password"));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/books")).StatusCode);
    }

    [Fact]
    public async Task The_last_admin_stays_an_admin()
    {
        var demoted = await _admin.PutAsJsonAsync($"/admin/readers/{factory.ReaderId}/admin", new AdminChange(false), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, demoted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.DeleteAsync($"/admin/readers/{factory.ReaderId}")).StatusCode);

        await factory.SignUpAsync("Ogion the Silent");
        var other = await factory.ReaderIdAsync("Ogion the Silent");
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PutAsJsonAsync($"/admin/readers/{other}/admin", new AdminChange(true), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PutAsJsonAsync($"/admin/readers/{other}/admin", new AdminChange(false), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PutAsJsonAsync("/admin/readers/999999/admin", new AdminChange(true), JsonOptions)).StatusCode);
    }

    [Fact]
    public async Task Removing_a_reader_takes_their_shelf_and_sends_borrowed_books_home()
    {
        var leaving = await factory.SignUpAsync("Serret");
        var leavingId = await factory.ReaderIdAsync("Serret");
        var theirs = await CreateAsync(leaving, new CreateBookRequest("Their Own Book", "Someone", BookStatus.Want, null));
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent(BooksEndpointTests.SampleEpub("Leaving soon.")), "file", "leaving.epub");
            await leaving.PostAsync($"/books/{theirs.Id}/ebook", content);
        }

        var ebookRoot = factory.Services.GetRequiredService<EbookStore>().Root;
        int Books() => Directory.GetFiles(ebookRoot).Count(path => !path.EndsWith(Fingerprint.Extension, StringComparison.Ordinal));
        Assert.NotEqual(0, Books());
        var before = Books();

        var lent = await CreateAsync(_admin, new CreateBookRequest("Lent Away", "Someone", BookStatus.Want, null));
        await _admin.PostAsJsonAsync($"/books/{lent.Id}/lend", new LendRequest(leavingId), JsonOptions);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/admin/readers/{leavingId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await leaving.GetAsync("/books")).StatusCode);
        Assert.Equal(before - 1, Books());
        var home = await _admin.GetFromJsonAsync<BookResponse>($"/books/{lent.Id}", JsonOptions);
        Assert.Null(home?.LoanedTo);
        Assert.Null(home?.BorrowerId);
        Assert.DoesNotContain(await _admin.GetFromJsonAsync<ReaderSummary[]>("/admin/readers", JsonOptions) ?? [], reader => reader.Id == leavingId);
    }

    [Fact]
    public async Task A_reader_can_delete_their_own_account_with_their_password()
    {
        var leaving = await factory.SignUpAsync("Hare");
        var wrong = await leaving.PostAsJsonAsync("/account/remove", new RemoveAccountRequest("not it"), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await leaving.GetAsync("/books")).StatusCode);

        var gone = await leaving.PostAsJsonAsync("/account/remove", new RemoveAccountRequest(ShelfApiFactory.Password), JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await leaving.GetAsync("/books")).StatusCode);
    }

    [Fact]
    public async Task A_reader_asks_to_borrow_from_an_open_shelf_and_the_owner_answers()
    {
        var owner = await factory.SignUpAsync("Tehanu");
        var ownerId = await factory.ReaderIdAsync("Tehanu");
        var wanted = await CreateAsync(owner, new CreateBookRequest("Wanted Book", "Someone", BookStatus.Finished, 5, Notes: "Private thoughts."));
        var declined = await CreateAsync(owner, new CreateBookRequest("Declined Book", "Someone", BookStatus.Want, null));
        var withdrawn = await CreateAsync(owner, new CreateBookRequest("Withdrawn Book", "Someone", BookStatus.Want, null));
        var outsider = await factory.SignUpAsync("Heather");

        // A closed shelf shows nothing and takes no asks.
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/books/shelves/{ownerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsync($"/books/{wanted.Id}/ask", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(true), JsonOptions)).StatusCode);
        Assert.Contains(await _admin.GetFromJsonAsync<OpenShelf[]>("/books/shelves", JsonOptions) ?? [], shelf => shelf.Id == ownerId && shelf.Books == 3);
        var raw = await _admin.GetStringAsync($"/books/shelves/{ownerId}");
        Assert.Contains("Wanted Book", raw);
        Assert.DoesNotContain("Private thoughts.", raw);
        Assert.Contains("Wanted Book", await _admin.GetStringAsync($"/shelves/{ownerId}"));

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"/books/{wanted.Id}/ask", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"/books/{wanted.Id}/ask", null)).StatusCode);
        await _admin.PostAsync($"/books/{declined.Id}/ask", null);
        await _admin.PostAsync($"/books/{withdrawn.Id}/ask", null);

        var incoming = (await owner.GetFromJsonAsync<LoanAsks>("/books/asks", JsonOptions))!.Incoming;
        Assert.Equal(3, incoming.Length);
        Assert.All(incoming, ask => Assert.Equal("Tenar", ask.Reader));
        var outgoing = (await _admin.GetFromJsonAsync<LoanAsks>("/books/asks", JsonOptions))!.Outgoing;
        Assert.Equal(["Declined Book", "Wanted Book", "Withdrawn Book"], outgoing.Select(ask => ask.Title).Order());
        Assert.Equal(3, (await owner.GetFromJsonAsync<Reminders>("/books/reminders", JsonOptions))?.Asks);
        Assert.Contains("asked to borrow", await owner.GetStringAsync("/library"));
        // Home names each ask, under Needs you, in place of the banner every other page has.
        var home = await owner.GetStringAsync("/");
        Assert.Contains("Needs you", home);
        Assert.Contains("Tenar asks to borrow Wanted Book", home);
        Assert.DoesNotContain("asked to borrow", home);
        Assert.Contains("Asked of you", await owner.GetStringAsync("/loans"));
        Assert.Contains(await _admin.GetFromJsonAsync<ShelfBook[]>($"/books/shelves/{ownerId}", JsonOptions) ?? [], book => book.Id == wanted.Id && book.Asked);

        // Only the owner or the reader who asked can answer an ask.
        var wantedAsk = incoming.Single(ask => ask.BookId == wanted.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"/books/asks/{wantedAsk.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/books/asks/{wantedAsk.Id}/lend", new LendAskRequest(), JsonOptions)).StatusCode);

        var due = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(21);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/books/asks/{wantedAsk.Id}/lend", new LendAskRequest(due), JsonOptions)).StatusCode);
        var borrowed = Assert.Single(await _admin.GetFromJsonAsync<BorrowedBook[]>("/books/borrowed", JsonOptions) ?? [], book => book.Id == wanted.Id);
        Assert.Equal(due, borrowed.DueOn);

        var declinedAsk = incoming.Single(ask => ask.BookId == declined.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/books/asks/{declinedAsk.Id}")).StatusCode);
        var withdrawnAsk = incoming.Single(ask => ask.BookId == withdrawn.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/books/asks/{withdrawnAsk.Id}")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<LoanAsks>("/books/asks", JsonOptions))!.Incoming);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(false), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/books/shelves/{ownerId}")).StatusCode);
    }

    [Fact]
    public async Task A_big_open_shelf_is_shown_a_page_at_a_time_and_searched()
    {
        var owner = await factory.SignUpAsync("Ogion");
        var ownerId = await factory.ReaderIdAsync("Ogion");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            db.Books.AddRange(Enumerable.Range(1, 130).Select(n => new Book { OwnerId = ownerId, Title = $"Volume {n:D3}", Author = n % 2 == 0 ? "Even Author" : "odd author", Status = BookStatus.Want }));
            await db.SaveChangesAsync();
        }

        await owner.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(true), JsonOptions);
        var first = System.Net.WebUtility.HtmlDecode(await _admin.GetStringAsync($"/shelves/{ownerId}"));
        Assert.Contains("1–60 of 130", first);
        Assert.Contains("Volume 002", first);
        Assert.DoesNotContain("Volume 001", first);
        Assert.Contains($"/shelves/{ownerId}?page=2", first);

        // By author, whatever their case, then title.
        var last = System.Net.WebUtility.HtmlDecode(await _admin.GetStringAsync($"/shelves/{ownerId}?page=3"));
        Assert.Contains("121–130 of 130", last);
        Assert.Contains("Volume 129", last);
        Assert.DoesNotContain("?page=4", last);

        var found = System.Net.WebUtility.HtmlDecode(await _admin.GetStringAsync($"/shelves/{ownerId}?q=volume+07"));
        Assert.Contains("Volume 070", found);
        Assert.Contains("Volume 079", found);
        Assert.DoesNotContain("Volume 080", found);
        Assert.Contains("No books here match that.", System.Net.WebUtility.HtmlDecode(await _admin.GetStringAsync($"/shelves/{ownerId}?q=nothing+like+it")));

        // As the library narrows and orders: by an author, by title, by what can be borrowed now, in another look.
        var byAuthor = System.Net.WebUtility.HtmlDecode(await _admin.GetStringAsync($"/shelves/{ownerId}?author=odd+author&sort=title"));
        Assert.Contains("1–60 of 65", byAuthor);
        Assert.Contains("Volume 001", byAuthor);
        Assert.DoesNotContain("Volume 002", byAuthor);
        Assert.Contains("By odd author", byAuthor);
        Assert.Contains("href=\"/shelves/" + ownerId + "?sort=title&author=odd%20author&page=2\"", byAuthor);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var lent = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.Books).First(book => book.OwnerId == ownerId && book.Title == "Volume 002");
            lent.LoanedTo = "A friend";
            await db.SaveChangesAsync();
        }

        var available = System.Net.WebUtility.HtmlDecode(await _admin.GetStringAsync($"/shelves/{ownerId}?has=available&view=compact"));
        Assert.Contains("Not lent out <span class=\"chip-count\">129</span>", available);
        Assert.Contains("class=\"book-table open-shelf\"", available);
        Assert.DoesNotContain("Volume 002", available);
        Assert.DoesNotContain(">E-book <span", available);
    }

    [Fact]
    public async Task Late_and_nearly_due_loans_are_called_out()
    {
        var borrower = await factory.SignUpAsync("Lark");
        var borrowerId = await factory.ReaderIdAsync("Lark");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var late = await CreateAsync(_admin, new CreateBookRequest("Long Overdue", "Someone", BookStatus.Want, null));
        var soon = await CreateAsync(_admin, new CreateBookRequest("Due Soon", "Someone", BookStatus.Want, null));
        await _admin.PostAsJsonAsync($"/books/{late.Id}/lend", new LendRequest(borrowerId, today.AddDays(-2)), JsonOptions);
        await _admin.PostAsJsonAsync($"/books/{soon.Id}/lend", new LendRequest(borrowerId, today.AddDays(2)), JsonOptions);

        var mine = await borrower.GetFromJsonAsync<Reminders>("/books/reminders", JsonOptions);
        Assert.Equal(1, mine?.BorrowedOverdue);
        Assert.Equal(1, mine?.BorrowedDueSoon);
        var page = await borrower.GetStringAsync("/library");
        Assert.Contains("1 book you borrowed is overdue", page);
        Assert.Contains("due within three days", page);
        var home = await borrower.GetStringAsync("/");
        Assert.Contains("Long Overdue was due back to", home);
        Assert.Contains("Due Soon is due back to", home);

        Assert.True((await _admin.GetFromJsonAsync<Reminders>("/books/reminders", JsonOptions))?.LentOverdue >= 1);
        Assert.Contains("Long Overdue is overdue from Lark", await _admin.GetStringAsync("/"));
    }

    [Fact]
    public async Task A_full_backup_carries_files_and_highlights_to_another_shelf()
    {
        var source = await factory.SignUpAsync("Kurremkarmerruk");
        var book = await CreateAsync(source, new CreateBookRequest("Packed Up", "Someone", BookStatus.Reading, null, Tags: ["kept"]));
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent(BooksEndpointTests.SampleEpub("Carried across.")), "file", "packed.epub");
            await source.PostAsync($"/books/{book.Id}/ebook", content);
        }

        using (var content = new MultipartFormDataContent())
        {
            content.Add(new StreamContent(BooksEndpointTests.ZipText("01 part.mp3", "a packed track")), "file", "packed.zip");
            await source.PostAsync($"/books/{book.Id}/audio", content);
        }

        await source.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("Carried", 0, "Kept in the backup."), JsonOptions);
        await source.PostAsJsonAsync($"/books/{book.Id}/quotes", new CreateQuoteRequest("Carried across.", null), JsonOptions);

        var json = await source.GetFromJsonAsync<LibraryExport>("/books/export", JsonOptions);
        Assert.Equal("Kept in the backup.", Assert.Single(json!.Books.Single().Highlights!).Note);

        var zip = await source.GetByteArrayAsync("/books/export/full");
        using (var archive = new ZipArchive(new MemoryStream(zip)))
        {
            Assert.NotNull(archive.GetEntry("shelf.json"));
            Assert.Contains(archive.Entries, entry => entry.FullName == "books/0001/ebook/packed.epub");
            Assert.Contains(archive.Entries, entry => entry.FullName.StartsWith("books/0001/audio/", StringComparison.Ordinal));
        }

        var target = await factory.SignUpAsync("Restored Reader");
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent(zip), "file", "backup.zip");
            var restored = await target.PostAsync("/books/import/full", content);
            Assert.Equal("/backup", restored.RequestMessage?.RequestUri?.AbsolutePath);
            Assert.Contains("restore=done&added=1&skipped=0&files=2", restored.RequestMessage?.RequestUri?.Query);
        }

        var copy = Assert.Single(await target.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions) ?? []);
        Assert.Equal("Packed Up", copy.Title);
        Assert.Equal(["kept"], copy.Tags);
        Assert.Single(copy.Quotes);
        Assert.Equal("Kept in the backup.", Assert.Single(copy.Highlights!).Note);
        Assert.Contains("Carried across.", await target.GetStringAsync($"/books/{copy.Id}/ebook/chapters/0"));
        Assert.Equal("a packed track", await target.GetStringAsync($"/books/{copy.Id}/audio/tracks/0"));

        // Restoring again adds nothing new.
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent(zip), "file", "backup.zip");
            var again = await target.PostAsync("/books/import/full", content);
            Assert.Contains("added=0&skipped=1", again.RequestMessage?.RequestUri?.Query);
        }

        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent("not a zip"u8.ToArray()), "file", "backup.zip");
            var broken = await target.PostAsync("/books/import/full", content);
            Assert.Contains("restore=unreadable", broken.RequestMessage?.RequestUri?.Query);
        }
    }

    [Fact]
    public async Task A_snapshot_holds_the_database_and_every_file()
    {
        // On PostgreSQL, the snapshot holds SQLite's file.
        if (ShelfApiFactory.OnPostgres)
        {
            return;
        }

        var book = await CreateAsync(_admin, new CreateBookRequest("In The Snapshot", "Someone", BookStatus.Want, null));
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent(BooksEndpointTests.SampleEpub("Snapshot text.")), "file", "snap.epub");
            await _admin.PostAsync($"/books/{book.Id}/ebook", content);
        }

        var response = await _admin.GetAsync("/admin/snapshot");
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("shelf-snapshot-", response.Content.Headers.ContentDisposition?.FileName);
        using var archive = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var database = archive.GetEntry("shelf.db");
        Assert.NotNull(database);
        Assert.True(database.Length > 0);
        Assert.Contains(archive.Entries, entry => entry.FullName.StartsWith("ebooks/", StringComparison.Ordinal) && entry.FullName.EndsWith(".epub", StringComparison.Ordinal));
    }

    // A backup without the database is never proved: not the note a snapshot leaves when pg_dump is missing, and not
    // the library as JSON earlier Shelfs put in its place, which held one reader's books at most (often none).
    [Fact]
    public async Task A_restore_drill_refuses_a_backup_without_the_database()
    {
        var folder = Directory.CreateTempSubdirectory("shelf-drill-").FullName;
        try
        {
            string Zip(string name, params (string Entry, string Text)[] entries)
            {
                var path = Path.Combine(folder, name);
                using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
                foreach (var (entry, text) in entries)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                    writer.Write(text);
                }

                return path;
            }

            var noDatabase = await BackupCheck.DrillPathAsync(Zip("none.zip", (Backup.NoDatabaseEntry, Backup.NoDatabaseProblem()), ("covers/a.jpg", "cover")));
            Assert.False(noDatabase.Ok);
            Assert.Equal(Backup.NoDatabaseProblem(), noDatabase.Problem);

            var library = await BackupCheck.DrillPathAsync(Zip("library.zip", ("library.json", "{\"books\":[{\"title\":\"One\",\"author\":\"Someone\"}]}")));
            Assert.False(library.Ok);
            Assert.Contains("pg_dump", library.Problem);

            var dump = await BackupCheck.DrillPathAsync(Zip("dump.zip", ("shelf.sql", "CREATE TABLE x ();\n-- PostgreSQL database dump complete\n")));
            Assert.True(dump.Ok, dump.Problem);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // On PostgreSQL with no pg_dump, the nightly backup keeps the files and fails its drill saying why, and the admin's
    // download is refused rather than handed over without the database. On SQLite, pg_dump plays no part.
    [Fact]
    public async Task Without_pg_dump_a_postgres_snapshot_says_it_holds_no_database()
    {
        var folder = Directory.CreateTempSubdirectory("shelf-nodump-").FullName;
        try
        {
            await using var app = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Backup:Folder", folder);
                builder.UseSetting("Database:PgDump", Path.Combine(folder, "no-such-pg_dump"));
            });
            var backup = await app.Services.GetRequiredService<BackupSchedule>().TakeAsync(CancellationToken.None);
            var verdict = app.Services.GetRequiredService<BackupCheck>().VerdictOf(backup.Name);
            Assert.NotNull(verdict);
            string[] entries;
            using (var zip = ZipFile.OpenRead(Path.Combine(folder, backup.Name)))
            {
                entries = [.. zip.Entries.Select(entry => entry.FullName)];
            }

            var admin = app.CreateClient();
            await ShelfApiFactory.PostFormAsync(admin, "/signin", "/account/signin", new() { ["name"] = "Tenar", ["password"] = ShelfApiFactory.Password });
            var snapshot = await admin.GetAsync("/admin/snapshot");

            if (ShelfApiFactory.OnPostgres)
            {
                Assert.Contains(Backup.NoDatabaseEntry, entries);
                Assert.DoesNotContain("library.json", entries);
                Assert.DoesNotContain("shelf.sql", entries);
                Assert.False(verdict.Ok);
                Assert.Equal(Backup.NoDatabaseProblem(), verdict.Problem);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, snapshot.StatusCode);
                Assert.Contains("pg_dump", await snapshot.Content.ReadAsStringAsync());
            }
            else
            {
                Assert.Contains("shelf.db", entries);
                Assert.True(verdict.Ok, verdict.Problem);
                Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
                Assert.Equal("application/zip", snapshot.Content.Headers.ContentType?.MediaType);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_library_opens_as_a_reader_last_left_it()
    {
        var reader = await factory.SignUpAsync("Sparrow");
        await CreateAsync(reader, new CreateBookRequest("A Listed Book", "Someone", BookStatus.Want, null));
        Assert.Contains("book-grid", await reader.GetStringAsync("/library"));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Sparrow"));
            await BookRules.SetLibraryAsListAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), true);
        }

        Assert.Contains("book-list", await reader.GetStringAsync("/library"));
        Assert.Contains("book-grid", await reader.GetStringAsync("/library?view=covers"));
        Assert.DoesNotContain("book-grid", await _admin.GetStringAsync("/library?view=list"));
    }

    [Fact]
    public async Task The_reader_sets_type_the_way_each_reader_chose()
    {
        var book = await CreateAsync(_admin, new CreateBookRequest("Set In Type", "Someone", BookStatus.Reading, null));
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent(BooksEndpointTests.SampleEpub("Well set.")), "file", "type.epub");
            await _admin.PostAsync($"/books/{book.Id}/ebook", content);
        }

        var plain = await _admin.GetStringAsync($"/books/{book.Id}/ebook/chapters/0");
        Assert.Contains("font-size: 100%", plain);
        Assert.Contains("prefers-color-scheme: dark", plain);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(factory.ReaderId);
            await BookRules.SetReaderTypeAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), new ReaderType(130, 190, 500));
        }

        var set = await _admin.GetStringAsync($"/books/{book.Id}/ebook/chapters/0");
        Assert.Contains("font-size: 130%", set);
        Assert.Contains("line-height: 1.9", set);
        Assert.Contains("max-width: 80em", set);
        Assert.Contains("Well set.", set);

        // The reader drops the sidebar for a slim bar and the text settings.
        var page = await _admin.GetStringAsync($"/library/{book.Id}/read");
        Assert.Contains("focus-bar", page);
        Assert.DoesNotContain("nav-main", page);
        Assert.Contains("Text settings", page);
        Assert.Contains("nav-main", await _admin.GetStringAsync($"/library/{book.Id}"));
    }

    [Fact]
    public async Task A_pdf_opens_in_the_shelf_reader_at_the_saved_page()
    {
        var book = await CreateAsync(_admin, new CreateBookRequest("A Plain Pdf", "Someone", BookStatus.Reading, null));
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent("%PDF-1.4 a plain one"u8.ToArray()), "file", "plain.pdf");
            await _admin.PostAsync($"/books/{book.Id}/ebook", content);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(factory.ReaderId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            await Lending.KeepPlaceAsync(db, (await Lending.OpenAsync(db, book.Id))!, place => place.EbookChapter = 4);
        }

        var page = await _admin.GetStringAsync($"/library/{book.Id}/read");
        Assert.Contains("pdf-viewer", page);
        Assert.Contains("Fit the width", page);
        Assert.Contains("page 5", page);
        Assert.DoesNotContain("<iframe", page);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync("/pdf-reader.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync("/lib/pdfjs/pdf.worker.min.mjs")).StatusCode);
    }

    [Fact]
    public async Task A_pdf_keeps_highlights_by_page()
    {
        var book = await CreateAsync(_admin, new CreateBookRequest("A Marked Pdf", "Someone", BookStatus.Reading, null));
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent("%PDF-1.4 sample"u8.ToArray()), "file", "marked.pdf");
            await _admin.PostAsync($"/books/{book.Id}/ebook", content);
        }

        var made = await _admin.PostAsJsonAsync(
            $"/books/{book.Id}/highlights",
            new CreateHighlightRequest("the ship leaves", 2, "Where it starts.", "Page one: ", " Anarres."),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        var bookPage = await _admin.GetStringAsync($"/library/{book.Id}");
        Assert.Contains("Page 3", bookPage);
        Assert.Contains($"/library/{book.Id}/read?chapter=2", bookPage);

        var reader = await _admin.GetStringAsync($"/library/{book.Id}/read?chapter=2");
        Assert.Contains("On this page", reader);
        Assert.Contains("Where it starts.", reader);
        Assert.Contains("page 3", reader);
    }

    [Fact]
    public async Task Large_uploads_report_their_progress()
    {
        var book = await CreateAsync(_admin, new CreateBookRequest("Uploaded Slowly", "Someone", BookStatus.Want, null));
        var page = await _admin.GetStringAsync($"/library/{book.Id}?tab=tab-copy");
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(page, "data-upload(?!-)").Count);
        Assert.Contains("data-upload-progress", page);
        Assert.Contains("data-upload", await _admin.GetStringAsync("/backup"));
        Assert.Contains("upload", await _admin.GetStringAsync("/upload.js"));
    }

    private static async Task<BookResponse> CreateAsync(HttpClient client, CreateBookRequest request)
    {
        var response = await client.PostAsJsonAsync("/books", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
    }
}
