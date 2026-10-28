using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed partial class ReadersTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _tenar = factory.Client;

    [Fact]
    public async Task A_visitor_who_is_not_signed_in_is_sent_to_sign_in()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var page = await client.GetAsync("/loans");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/signin", page.Headers.Location?.AbsolutePath);
        Assert.Contains("ReturnUrl=%2Floans", page.Headers.Location?.Query);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/books")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/books/sync")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/signin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/signup")).StatusCode);
    }

    [Fact]
    public async Task Sign_up_refuses_a_taken_address_a_missing_one_a_short_password_and_a_mismatch()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var taken = await SignUpFormAsync(client, "Ged", "TENAR@example.test", ShelfApiFactory.Password, ShelfApiFactory.Password);
        Assert.Contains("problem=EmailTaken", taken.Headers.Location?.OriginalString);

        var missing = await SignUpFormAsync(client, "Ged", "", ShelfApiFactory.Password, ShelfApiFactory.Password);
        Assert.Contains("problem=EmailMissing", missing.Headers.Location?.OriginalString);

        var invalid = await SignUpFormAsync(client, "Ged", "not an address", ShelfApiFactory.Password, ShelfApiFactory.Password);
        Assert.Contains("problem=EmailInvalid", invalid.Headers.Location?.OriginalString);

        var weak = await SignUpFormAsync(client, "Ged", "ged@example.test", "short", "short");
        Assert.Contains("problem=PasswordTooShort", weak.Headers.Location?.OriginalString);

        var mismatch = await SignUpFormAsync(client, "Ged", "ged@example.test", ShelfApiFactory.Password, "something else entirely");
        Assert.Contains("problem=PasswordsDiffer", mismatch.Headers.Location?.OriginalString);

        var page = await client.GetStringAsync(mismatch.Headers.Location!.OriginalString);
        Assert.Contains("The two passwords are different.", page);
        Assert.Contains("value=\"Ged\"", page);
        Assert.Contains("value=\"ged@example.test\"", page);
    }

    [Fact]
    public async Task Readers_sign_in_by_email_and_may_share_a_name()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal("/", (await SignUpFormAsync(client, "Shared Name", "first.shared@example.test", ShelfApiFactory.Password, ShelfApiFactory.Password)).Headers.Location?.OriginalString);
        var other = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal("/", (await SignUpFormAsync(other, "shared name", "second.shared@example.test", ShelfApiFactory.Password + "2", ShelfApiFactory.Password + "2")).Headers.Location?.OriginalString);

        // The address finds each, whatever its case; the name, now two readers', finds neither.
        var visitor = factory.CreateClient(new() { AllowAutoRedirect = false });
        var byEmail = await ShelfApiFactory.PostFormAsync(visitor, "/signin", "/account/signin", new() { ["email"] = "Second.Shared@Example.test", ["password"] = ShelfApiFactory.Password + "2" });
        Assert.Equal("/", byEmail.Headers.Location?.OriginalString);
        var byName = await ShelfApiFactory.PostFormAsync(factory.CreateClient(new() { AllowAutoRedirect = false }), "/signin", "/account/signin", new() { ["email"] = "Shared Name", ["password"] = ShelfApiFactory.Password });
        Assert.Contains("problem=WrongPassword", byName.Headers.Location?.OriginalString);

        // A name only one reader has still signs in, as KOReader and Audiobookshelf apps already set up use it.
        var unique = await ShelfApiFactory.PostFormAsync(factory.CreateClient(new() { AllowAutoRedirect = false }), "/signin", "/account/signin", new() { ["name"] = "Tenar", ["password"] = ShelfApiFactory.Password });
        Assert.Equal("/", unique.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task An_address_can_change_to_a_free_one_but_not_be_removed()
    {
        var reader = await factory.SignUpAsync("Changing Address");
        var taken = await reader.PutAsJsonAsync("/account/email", new EmailSettingsRequest("tenar@example.test", true), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, taken.StatusCode);
        Assert.Contains("already signs in with that email address", await taken.Content.ReadAsStringAsync());

        var removed = await reader.PutAsJsonAsync("/account/email", new EmailSettingsRequest(null, true), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, removed.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync("/account/email", new EmailSettingsRequest("moved@example.test", true), JsonOptions)).StatusCode);
        var visitor = factory.CreateClient(new() { AllowAutoRedirect = false });
        var signedIn = await ShelfApiFactory.PostFormAsync(visitor, "/signin", "/account/signin", new() { ["email"] = "moved@example.test", ["password"] = ShelfApiFactory.Password });
        Assert.Equal("/", signedIn.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_new_address_is_only_the_readers_once_they_open_the_link_sent_to_it()
    {
        await using var addressed = factory.WithWebHostBuilder(builder => builder.UseSetting("Email:PublicAddress", "https://shelf.test"));
        var first = addressed.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal("/", (await SignUpFormAsync(first, "Claimant One", "claimed@example.test", ShelfApiFactory.Password, ShelfApiFactory.Password)).Headers.Location?.OriginalString);

        // Not confirmed, the address is no one's yet: someone else may give it too, and each signs in with it by
        // their own password.
        var second = addressed.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal("/", (await SignUpFormAsync(second, "Claimant Two", "claimed@example.test", ShelfApiFactory.Password + "2", ShelfApiFactory.Password + "2")).Headers.Location?.OriginalString);
        var links = factory.Mail.Sent.Where(mail => mail.To == "claimed@example.test").Select(mail => ConfirmLink().Match(mail.Body).Value).ToList();
        Assert.Equal(2, links.Count);
        var visitor = addressed.CreateClient(new() { AllowAutoRedirect = false });
        var asSecond = await ShelfApiFactory.PostFormAsync(visitor, "/signin", "/account/signin", new() { ["email"] = "claimed@example.test", ["password"] = ShelfApiFactory.Password + "2" });
        Assert.Equal("/", asSecond.Headers.Location?.OriginalString);
        Assert.Contains("Claimant Two", await visitor.GetStringAsync("/account"));

        // The second opens their link first, and has it; the first's link then finds it taken.
        Assert.Equal("/account?confirmed=1#email", (await second.GetAsync(links[1])).Headers.Location?.OriginalString);
        Assert.Equal("/signin?problem=EmailTaken", (await addressed.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(links[0])).Headers.Location?.OriginalString);
        Assert.Equal("/signin?problem=ConfirmExpired", (await addressed.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(links[1])).Headers.Location?.OriginalString);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.Equal("Claimant Two", (await ReaderRules.FindAsync(db, "claimed@example.test"))?.Name);
            var one = await db.Readers.SingleAsync(reader => reader.Name == "Claimant One");
            Assert.Equal((null, null), (one.Email, one.PendingEmail));
        }

        // A change waits for its link too, and the address before it keeps working until then.
        var change = await second.PutAsJsonAsync("/account/email", new EmailSettingsRequest("moved.claimant@example.test", true), JsonOptions);
        Assert.Equal(HttpStatusCode.Accepted, change.StatusCode);
        Assert.Equal("/", (await ShelfApiFactory.PostFormAsync(addressed.CreateClient(new() { AllowAutoRedirect = false }), "/signin", "/account/signin", new() { ["email"] = "claimed@example.test", ["password"] = ShelfApiFactory.Password + "2" })).Headers.Location?.OriginalString);
        var moved = ConfirmLink().Match(factory.Mail.Sent.Last(mail => mail.To == "moved.claimant@example.test").Body).Value;
        Assert.Equal("/account?confirmed=1#email", (await second.GetAsync(moved)).Headers.Location?.OriginalString);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.Equal("Claimant Two", (await ReaderRules.FindAsync(db, "moved.claimant@example.test"))?.Name);
            Assert.Null(await ReaderRules.FindAsync(db, "claimed@example.test"));
        }
    }

    [Fact]
    public async Task Readers_who_share_a_name_are_told_apart_by_a_hint_of_their_address()
    {
        await factory.SignUpAsync("Twin Reader");
        var other = factory.CreateClient(new() { AllowAutoRedirect = false });
        await SignUpFormAsync(other, "Twin Reader", "samantha@example.org", ShelfApiFactory.Password, ShelfApiFactory.Password);
        var names = (await _tenar.GetFromJsonAsync<ReaderResponse[]>("/readers", JsonOptions))!.Select(reader => reader.Name).ToList();
        Assert.Contains("Twin Reader (t…r@example.test)", names);
        Assert.Contains("Twin Reader (s…a@example.org)", names);
        Assert.DoesNotContain(names, name => name.Contains("samantha", StringComparison.Ordinal));
        Assert.Contains("Tenar", (await other.GetFromJsonAsync<ReaderResponse[]>("/readers", JsonOptions))!.Select(reader => reader.Name));
    }

    [Fact]
    public async Task Addresses_given_before_sign_in_was_by_email_become_ways_to_sign_in_first_come_first()
    {
        await using var fresh = new FreshShelfFactory();
        await using var scope = fresh.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        Reader Old(string name, string? email) => new() { Name = name, NormalizedName = ReaderRules.Normalize(name), Email = email, CreatedAt = DateTimeOffset.UtcNow };
        db.Readers.AddRange(Old("Older", "kept@example.test"), Old("Younger", "Kept@example.test"), Old("Addressless", null));
        await db.SaveChangesAsync();

        await ReaderRules.AdoptEmailsAsync(db);
        Assert.Equal("Older", (await ReaderRules.FindAsync(db, "KEPT@example.test"))?.Name);
        Assert.Null(db.Readers.Single(reader => reader.Name == "Younger").NormalizedEmail);
        Assert.Equal("Younger", (await ReaderRules.FindAsync(db, "younger"))?.Name);
        Assert.Equal("Addressless", (await ReaderRules.FindAsync(db, "Addressless"))?.Name);
    }

    [Fact]
    public async Task Sign_in_checks_the_password_and_returns_to_a_local_page_only()
    {
        await factory.SignUpAsync("Ogion");
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var wrong = await ShelfApiFactory.PostFormAsync(client, "/signin", "/account/signin", new()
        {
            ["name"] = "Ogion",
            ["password"] = "not the password",
        });
        Assert.Contains("problem=WrongPassword", wrong.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/books")).StatusCode);

        var away = await ShelfApiFactory.PostFormAsync(client, "/signin", "/account/signin", new()
        {
            ["name"] = "  ogion ",
            ["password"] = ShelfApiFactory.Password,
            ["returnUrl"] = "//elsewhere.example/",
        });
        Assert.Equal("/", away.Headers.Location?.OriginalString);

        var back = await ShelfApiFactory.PostFormAsync(client, "/signin", "/account/signin", new()
        {
            ["name"] = "Ogion",
            ["password"] = ShelfApiFactory.Password,
            ["returnUrl"] = "/loans",
        });
        Assert.Equal("/loans", back.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/books")).StatusCode);
        Assert.Contains("Sign out", await client.GetStringAsync("/"));

        var signedOut = await ShelfApiFactory.PostFormAsync(client, "/", "/account/signout", []);
        Assert.Equal("/signin", signedOut.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/books")).StatusCode);
    }

    [Fact]
    public async Task Each_reader_sees_only_their_own_shelf()
    {
        var other = await factory.SignUpAsync("Arha");
        var book = await CreateAsync(_tenar, new CreateBookRequest(
            "The Tombs of Atuan", "Ursula K. Le Guin", BookStatus.Finished, 5, Tags: ["earthsea"]));
        await _tenar.PostAsJsonAsync($"/books/{book.Id}/quotes", new CreateQuoteRequest("The Place of the Tombs.", null), JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/books/{book.Id}")).StatusCode);
        Assert.DoesNotContain(await other.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions) ?? [], item => item.Id == book.Id);
        Assert.Empty(await other.GetFromJsonAsync<QuoteListItem[]>("/books/quotes", JsonOptions) ?? []);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(
            $"/books/{book.Id}",
            new UpdateBookRequest("Taken", "Someone", BookStatus.Want, null),
            JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/books/{book.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(
            $"/books/{book.Id}/quotes",
            new CreateQuoteRequest("Not mine.", null),
            JsonOptions)).StatusCode);
        Assert.DoesNotContain("The Tombs of Atuan", await other.GetStringAsync("/"));

        var stats = await other.GetFromJsonAsync<ShelfStatsResponse>("/books/stats", JsonOptions);
        Assert.Equal(0, stats?.Total);
        Assert.Empty(stats!.Tags);

        await other.PutAsJsonAsync("/settings", new UpdateSettingsRequest(7), JsonOptions);
        Assert.Equal(7, (await other.GetFromJsonAsync<ShelfStatsResponse>("/books/stats", JsonOptions))?.YearlyGoal);
        Assert.NotEqual(7, (await _tenar.GetFromJsonAsync<ShelfStatsResponse>("/books/stats", JsonOptions))?.YearlyGoal);

        var mine = await _tenar.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions);
        Assert.Equal("The Tombs of Atuan", mine?.Title);
        Assert.Single(mine!.Quotes);
    }

    [Fact]
    public async Task A_tag_stays_while_another_reader_still_uses_it()
    {
        var other = await factory.SignUpAsync("Lebannen");
        var kept = await CreateAsync(_tenar, new CreateBookRequest("The Farthest Shore", "Ursula K. Le Guin", BookStatus.Want, null, Tags: ["dragons"]));
        var theirs = await CreateAsync(other, new CreateBookRequest("Tehanu", "Ursula K. Le Guin", BookStatus.Want, null, Tags: ["dragons"]));

        Assert.Equal(HttpStatusCode.NoContent, (await other.DeleteAsync($"/books/{theirs.Id}")).StatusCode);

        var book = await _tenar.GetFromJsonAsync<BookResponse>($"/books/{kept.Id}", JsonOptions);
        Assert.Equal(["dragons"], book!.Tags);
        var found = await _tenar.GetFromJsonAsync<BookResponse[]>("/books?tag=dragons", JsonOptions);
        Assert.Contains(found!, item => item.Id == kept.Id);
    }

    [Fact]
    public async Task A_book_lent_to_a_reader_shows_on_their_loans_until_it_comes_back()
    {
        var borrower = await factory.SignUpAsync("Therru");
        var borrowerId = await factory.ReaderIdAsync("Therru");
        var book = await CreateAsync(_tenar, new CreateBookRequest("The Other Wind", "Ursula K. Le Guin", BookStatus.Finished, 4));

        var readers = await _tenar.GetFromJsonAsync<ReaderResponse[]>("/readers", JsonOptions);
        Assert.Contains(readers!, reader => reader.Id == borrowerId && reader.Name == "Therru");
        Assert.DoesNotContain(readers!, reader => reader.Id == factory.ReaderId);

        var due = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14);
        var lent = await _tenar.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(borrowerId, due), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, lent.StatusCode);
        var loaned = await lent.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.Equal("Therru", loaned?.LoanedTo);
        Assert.Equal(borrowerId, loaned?.BorrowerId);
        Assert.Equal(due, loaned?.DueOn);
        Assert.NotNull(loaned?.LoanedOn);

        var borrowed = await borrower.GetFromJsonAsync<BorrowedBook[]>("/books/borrowed", JsonOptions);
        var entry = Assert.Single(borrowed!);
        Assert.Equal("The Other Wind", entry.Title);
        Assert.Equal("Tenar", entry.Owner);
        Assert.Equal(due, entry.DueOn);
        Assert.Contains("The Other Wind", await borrower.GetStringAsync("/loans"));
        Assert.Contains("From Tenar", await borrower.GetStringAsync("/loans"));

        // Borrowing does not put the book on the borrower's own shelf.
        Assert.Equal(HttpStatusCode.NotFound, (await borrower.GetAsync($"/books/{book.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await borrower.PostAsJsonAsync(
            $"/books/{book.Id}/lend", new LendRequest(factory.ReaderId), JsonOptions)).StatusCode);
        Assert.Empty(await _tenar.GetFromJsonAsync<BorrowedBook[]>("/books/borrowed", JsonOptions) ?? []);

        Assert.Equal(HttpStatusCode.NoContent, (await borrower.PostAsync($"/books/borrowed/{book.Id}/return", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await borrower.PostAsync($"/books/borrowed/{book.Id}/return", null)).StatusCode);
        Assert.Empty(await borrower.GetFromJsonAsync<BorrowedBook[]>("/books/borrowed", JsonOptions) ?? []);
        var back = await _tenar.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions);
        Assert.Null(back?.LoanedTo);
        Assert.Null(back?.BorrowerId);
        Assert.Null(back?.DueOn);

        await _tenar.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(borrowerId), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, (await _tenar.PostAsync($"/books/{book.Id}/return", null)).StatusCode);
        Assert.Empty(await borrower.GetFromJsonAsync<BorrowedBook[]>("/books/borrowed", JsonOptions) ?? []);
    }

    [Fact]
    public async Task Lending_refuses_the_owner_a_stranger_and_a_book_already_out()
    {
        var borrowerId = (await factory.SignUpAsync("Alder"), await factory.ReaderIdAsync("Alder")).Item2;
        var book = await CreateAsync(_tenar, new CreateBookRequest("Tales from Earthsea", "Ursula K. Le Guin", BookStatus.Want, null));

        var self = await _tenar.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(factory.ReaderId), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        var stranger = await _tenar.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(999_999), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);

        var withName = BookWrite.From(book) with { LoanedTo = "A neighbour" };
        await _tenar.PutAsJsonAsync($"/books/{book.Id}", ToUpdate(withName), JsonOptions);
        var busy = await _tenar.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(borrowerId), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, busy.StatusCode);
    }

    [Fact]
    public async Task Typing_another_name_over_a_reader_loan_ends_it_for_that_reader()
    {
        var borrower = await factory.SignUpAsync("Irian");
        var borrowerId = await factory.ReaderIdAsync("Irian");
        var book = await CreateAsync(_tenar, new CreateBookRequest("The Wind's Twelve Quarters", "Ursula K. Le Guin", BookStatus.Want, null));
        var lent = await (await _tenar.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(borrowerId), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions);

        // Saving the book unchanged keeps the loan with the reader.
        var same = await _tenar.PutAsJsonAsync($"/books/{book.Id}", ToUpdate(BookWrite.From(lent!)), JsonOptions);
        Assert.Equal(borrowerId, (await same.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))?.BorrowerId);
        Assert.Single(await borrower.GetFromJsonAsync<BorrowedBook[]>("/books/borrowed", JsonOptions) ?? []);

        var renamed = await _tenar.PutAsJsonAsync($"/books/{book.Id}", ToUpdate(BookWrite.From(lent!) with { LoanedTo = "Someone else" }), JsonOptions);
        var saved = await renamed.Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        Assert.Equal("Someone else", saved?.LoanedTo);
        Assert.Null(saved?.BorrowerId);
        Assert.Empty(await borrower.GetFromJsonAsync<BorrowedBook[]>("/books/borrowed", JsonOptions) ?? []);
    }

    [Fact]
    public async Task A_borrower_reads_and_listens_with_their_own_place_and_notes()
    {
        var borrower = await factory.SignUpAsync("Sparrowhawk");
        var borrowerId = await factory.ReaderIdAsync("Sparrowhawk");
        var stranger = await factory.SignUpAsync("Cob");
        var book = await CreateAsync(_tenar, new CreateBookRequest("The Beginning Place", "Ursula K. Le Guin", BookStatus.Reading, null));
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent(BooksEndpointTests.SampleEpub("A gate in the twilight.")), "file", "place.epub");
            await _tenar.PostAsync($"/books/{book.Id}/ebook", content);
        }

        using (var content = new MultipartFormDataContent())
        {
            content.Add(new StreamContent(BooksEndpointTests.ZipText("one.mp3", "first track")), "file", "place.zip");
            await _tenar.PostAsync($"/books/{book.Id}/audio", content);
        }

        await _tenar.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("A gate", 0, "Noted by the owner."), JsonOptions);

        // Before the loan, the book's files are the owner's alone.
        Assert.Equal(HttpStatusCode.NotFound, (await borrower.GetAsync($"/books/{book.Id}/ebook/file")).StatusCode);
        await _tenar.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(borrowerId), JsonOptions);

        var listed = Assert.Single(await borrower.GetFromJsonAsync<BorrowedBook[]>("/books/borrowed", JsonOptions) ?? []);
        Assert.True(listed.HasEbook);
        Assert.True(listed.HasAudio);
        Assert.Equal(HttpStatusCode.OK, (await borrower.GetAsync($"/books/{book.Id}/ebook/file")).StatusCode);
        Assert.Equal("first track", await borrower.GetStringAsync($"/books/{book.Id}/audio/tracks/0"));
        var chapter = await borrower.GetStringAsync($"/books/{book.Id}/ebook/chapters/0");
        Assert.Contains("A gate in the twilight.", chapter);
        Assert.DoesNotContain("Noted by the owner.", chapter);
        var reading = await borrower.GetStringAsync($"/library/{book.Id}/read");
        Assert.Contains("lent to you", reading);
        Assert.Contains("shelf-epub", reading);
        Assert.Contains($"/books/{book.Id}/ebook/chapters/0", await borrower.GetStringAsync($"/library/{book.Id}/read?classic=1"));
        Assert.Contains("lent to you", await borrower.GetStringAsync($"/library/{book.Id}/listen"));
        Assert.Equal($"/books/{book.Id}/audio/tracks/0", (await borrower.GetFromJsonAsync<AudioPlan>($"/books/{book.Id}/audio/plan", JsonOptions))!.Tracks[0].Url);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/audio/plan")).StatusCode);
        Assert.Contains($"/library/{book.Id}/read", await borrower.GetStringAsync("/loans"));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/ebook/file")).StatusCode);

        // Each side keeps its own notes.
        Assert.Empty(await borrower.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions) ?? []);
        var made = await borrower.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("twilight", 0, "Noted by the borrower."), JsonOptions);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Contains("Noted by the borrower.", await borrower.GetStringAsync($"/books/{book.Id}/ebook/chapters/0"));
        var owners = await _tenar.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions);
        Assert.Equal("Noted by the owner.", Assert.Single(owners!).Note);
        Assert.DoesNotContain("Noted by the borrower.", await _tenar.GetStringAsync($"/library/{book.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await borrower.DeleteAsync($"/books/{book.Id}/highlights/{owners![0].Id}")).StatusCode);

        // The borrower's place does not move the owner's.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(borrowerId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var open = await Lending.OpenAsync(db, book.Id);
            Assert.True(open?.Borrowed);
            await Lending.KeepPlaceAsync(db, open!, place =>
            {
                place.AudioTrack = 0;
                place.AudioSeconds = 42;
            });
            Assert.Equal(42, (await Lending.PlaceAsync(db, open!)).AudioSeconds);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(factory.ReaderId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var open = await Lending.OpenAsync(db, book.Id);
            Assert.False(open?.Borrowed);
            Assert.Equal(0, (await Lending.PlaceAsync(db, open!)).AudioSeconds);
        }

        // Once it comes back, the borrower cannot open it.
        await borrower.PostAsync($"/books/borrowed/{book.Id}/return", null);
        Assert.Equal(HttpStatusCode.NotFound, (await borrower.GetAsync($"/books/{book.Id}/ebook/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await borrower.GetAsync($"/books/{book.Id}/audio/tracks/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await borrower.GetAsync($"/books/{book.Id}/highlights")).StatusCode);
    }

    [Fact]
    public async Task A_device_key_opens_only_the_sync_of_its_own_shelf()
    {
        var other = await factory.SignUpAsync("Kalessin");
        await CreateAsync(other, new CreateBookRequest("The Lathe of Heaven", "Ursula K. Le Guin", BookStatus.Want, null));
        var mine = await CreateAsync(_tenar, new CreateBookRequest("Always Coming Home", "Ursula K. Le Guin", BookStatus.Want, null));
        using (var content = new MultipartFormDataContent())
        {
            content.Add(new ByteArrayContent("%PDF-1.4 sample"u8.ToArray()), "file", "home.pdf");
            await _tenar.PostAsync($"/books/{mine.Id}/ebook", content);
        }

        string key;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            key = await ReaderRules.NewKeyAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), factory.ReaderId);
        }

        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var catalog = await device.GetFromJsonAsync<SyncCatalog>("/books/sync", JsonOptions);
        Assert.Contains(catalog!.Books, item => item.Title == "Always Coming Home");
        Assert.DoesNotContain(catalog.Books, item => item.Title == "The Lathe of Heaven");
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/books")).StatusCode);

        var offered = await device.PostAsJsonAsync("/books/sync/books", new SyncOffer("The Word for World Is Forest", "Ursula K. Le Guin", null), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, offered.StatusCode);
        Assert.Contains(await _tenar.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions) ?? [], item => item.Title == "The Word for World Is Forest");
        Assert.DoesNotContain(await other.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions) ?? [], item => item.Title == "The Word for World Is Forest");

        var stranger = factory.CreateClient();
        stranger.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/books/sync")).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await ReaderRules.NewKeyAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), factory.ReaderId);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/books/sync")).StatusCode);
    }

    [Fact]
    public async Task The_first_reader_keeps_the_books_from_before_accounts()
    {
        await using var fresh = new FreshShelfFactory();
        await using (var scope = fresh.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            db.Books.Add(new Book { Title = "Planet of Exile", Author = "Ursula K. Le Guin", AddedAt = DateTimeOffset.UtcNow });
            db.Settings.Add(new ShelfSetting { Id = 1, YearlyGoal = 30 });
            await db.SaveChangesAsync();
        }

        var first = await fresh.SignUpAsync("Rolery");
        var books = await first.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions);
        Assert.Contains(books!, item => item.Title == "Planet of Exile");
        Assert.Equal(30, (await first.GetFromJsonAsync<ShelfStatsResponse>("/books/stats", JsonOptions))?.YearlyGoal);

        var second = await fresh.SignUpAsync("Agat");
        Assert.Empty(await second.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions) ?? []);

        await using (var scope = fresh.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.False(await db.Books.IgnoreQueryFilters().AnyAsync(book => book.OwnerId == null));
        }
    }

    private static UpdateBookRequest ToUpdate(BookWrite write) => new(
        write.Title, write.Author, write.Status, write.Rating, write.Year, write.Isbn, write.Pages, write.CurrentPage,
        write.Notes, write.StartedOn, write.FinishedOn, write.LoanedTo, write.Tags.ToArray(), write.Subtitle, write.Publisher,
        write.Language, write.Format, write.Series, write.SeriesNumber, write.CoverUrl, write.Review, write.Loved, write.LoanedOn,
        write.Location, write.AcquiredOn, write.Translator, write.OriginalTitle, write.RecommendedBy, write.Inscription,
        write.DueOn, write.Queued, write.Acquisition, write.Condition);

    private static Task<HttpResponseMessage> SignUpFormAsync(HttpClient client, string name, string email, string password, string confirm) =>
        ShelfApiFactory.PostFormAsync(client, "/signup", "/account/signup", new()
        {
            ["name"] = name,
            ["email"] = email,
            ["password"] = password,
            ["confirm"] = confirm,
        });

    private static async Task<BookResponse> CreateAsync(HttpClient client, CreateBookRequest request)
    {
        var response = await client.PostAsJsonAsync("/books", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
    }

    // A shelf with no readers yet, for the claim on the first sign-up.
    private sealed class FreshShelfFactory : IAsyncDisposable
    {
        private readonly ShelfApiFactory _inner = new();

        public IServiceProvider Services => _inner.Services;

        public Task<HttpClient> SignUpAsync(string name) => _inner.SignUpAsync(name);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"/account/email/confirm\?token=[A-Za-z0-9_-]+")]
    private static partial System.Text.RegularExpressions.Regex ConfirmLink();

    // Every route of a book, asked by a reader it was never lent to, answers that there is no such book: none gives
    // the book or anything of it, and none falls over. Deleting it is refused the same, and the book is still there.
    [Fact]
    public async Task No_route_of_a_book_opens_it_to_a_reader_it_isnt_lent_to()
    {
        var owner = await factory.SignUpAsync("Sweep Owner");
        var stranger = await factory.SignUpAsync("Sweep Stranger");
        var book = (await (await owner.PostAsJsonAsync("/books", new CreateBookRequest("Kept To Myself", "Someone", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(KoreaderHighlightsTests.Epub()), "file", "kept.epub" } })
        {
            (await owner.PostAsync($"/books/{book.Id}/ebook", content)).EnsureSuccessStatusCode();
        }

        await owner.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("Before.", 0, "A private note."), JsonOptions);

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(endpoint => (Pattern: endpoint.RoutePattern, Methods: endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []))
            .Where(route => route.Pattern.RawText is { } raw && raw.StartsWith("/books/{id:int}", StringComparison.Ordinal)
                && route.Pattern.Parameters.All(parameter => parameter.Name == "id"))
            .ToList();
        Assert.True(routes.Count > 40, $"Only {routes.Count} routes of a book were found.");

        var opened = new List<string>();
        foreach (var (pattern, methods) in routes)
        {
            var path = pattern.RawText!.Replace("{id:int}", book.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            foreach (var method in methods.Where(method => method is "GET" or "DELETE"))
            {
                var answer = await stranger.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
                if (answer.IsSuccessStatusCode || (int)answer.StatusCode >= 500)
                {
                    opened.Add($"{method} {path} -> {(int)answer.StatusCode}");
                }
            }
        }

        Assert.True(opened.Count == 0, string.Join("\n", opened));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/books/{book.Id}")).StatusCode);
        Assert.Equal("A private note.", Assert.Single(await owner.GetFromJsonAsync<HighlightResponse[]>($"/books/{book.Id}/highlights", JsonOptions) ?? []).Note);
    }
}
