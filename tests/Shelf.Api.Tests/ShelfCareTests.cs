using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        Assert.Contains("Download a snapshot", await _admin.GetStringAsync("/admin"));
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
        Assert.NotEmpty(Directory.GetFiles(ebookRoot));
        var before = Directory.GetFiles(ebookRoot).Length;

        var lent = await CreateAsync(_admin, new CreateBookRequest("Lent Away", "Someone", BookStatus.Want, null));
        await _admin.PostAsJsonAsync($"/books/{lent.Id}/lend", new LendRequest(leavingId), JsonOptions);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/admin/readers/{leavingId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await leaving.GetAsync("/books")).StatusCode);
        Assert.Equal(before - 1, Directory.GetFiles(ebookRoot).Length);
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
        Assert.Contains("asked to borrow", await owner.GetStringAsync("/"));
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
        var page = await borrower.GetStringAsync("/");
        Assert.Contains("1 book you borrowed is overdue", page);
        Assert.Contains("due within three days", page);

        Assert.True((await _admin.GetFromJsonAsync<Reminders>("/books/reminders", JsonOptions))?.LentOverdue >= 1);
        Assert.Contains("overdue", await _admin.GetStringAsync("/"));
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
            Assert.Equal("/stats", restored.RequestMessage?.RequestUri?.AbsolutePath);
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

    private static async Task<BookResponse> CreateAsync(HttpClient client, CreateBookRequest request)
    {
        var response = await client.PostAsJsonAsync("/books", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
    }
}
