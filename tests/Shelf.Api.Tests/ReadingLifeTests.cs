using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

// A reading life: books read again, the journal, the lines drawn between books, plans, pictures of a copy, and
// club meetings.
public sealed class ReadingLifeTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_book_read_again_keeps_each_read_and_counts_in_each_year()
    {
        var reader = await factory.SignUpAsync("Rereading Reader");
        var book = await CreateAsync(reader, new CreateBookRequest("Middlemarch", "George Eliot", BookStatus.Finished, 4, Pages: 880, StartedOn: new DateOnly(2019, 1, 2), FinishedOn: new DateOnly(2019, 3, 1)));
        await reader.PostAsJsonAsync($"/books/{book.Id}/sessions", new CreateSessionRequest(new DateOnly(2019, 2, 1), 1, 200, "Dorothea's mistake."), JsonOptions);
        var other = await CreateAsync(reader, new CreateBookRequest("Persuasion", "Jane Austen", BookStatus.Reading, null));

        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsync($"/books/{other.Id}/again", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync($"/books/{book.Id}/again", null)).StatusCode);
        var now = (await reader.GetFromJsonAsync<BookResponse>($"/books/{book.Id}", JsonOptions))!;
        Assert.Equal((BookStatus.Reading, (DateOnly?)DateOnly.FromDateTime(DateTime.UtcNow), (DateOnly?)null, (int?)4), (now.Status, now.StartedOn, now.FinishedOn, now.Rating));

        var reads = (await reader.GetFromJsonAsync<ReadThrough[]>($"/books/{book.Id}/reads", JsonOptions))!;
        Assert.Equal(2, reads.Length);
        Assert.Equal((1, (DateOnly?)new DateOnly(2019, 3, 1), (int?)4, 1, false), (reads[0].Number, reads[0].FinishedOn, reads[0].Rating, reads[0].Sessions, reads[0].Current));
        Assert.Equal((2, 0, true), (reads[1].Number, reads[1].Sessions, reads[1].Current));
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync($"/books/{book.Id}/reads/{reads[0].PastReadId}", new PastReadRequest(3, "I sided with Casaubon less this time."), JsonOptions)).StatusCode);

        // Finishing it again: the year of the first read still counts it, as does this year.
        var update = BookWrite.From(now) with { Status = BookStatus.Finished, Rating = 5 };
        await reader.PutAsJsonAsync($"/books/{book.Id}", ToUpdate(update), JsonOptions);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Rereading Reader"));
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            Assert.Equal(1, (await YearReview.BuildAsync(db, 2019)).Finished);
            Assert.Equal(1, (await YearReview.BuildAsync(db, DateTime.UtcNow.Year)).Finished);
            Assert.Contains(2019, await YearReview.YearsAsync(db));
        }

        var stats = (await reader.GetFromJsonAsync<ShelfStatsResponse>("/books/stats", JsonOptions))!;
        Assert.Equal(1, stats.FinishedThisYear);

        // Both reads travel in a backup.
        var export = (await reader.GetFromJsonAsync<LibraryExport>("/books/export", JsonOptions))!;
        var past = Assert.Single(export.Books.Single(item => item.Title == "Middlemarch").PastReads!);
        Assert.Equal(("I sided with Casaubon less this time.", (int?)3), (past.Thoughts, past.Rating));
    }

    [Fact]
    public async Task The_journal_gathers_a_year_and_finds_words_across_years()
    {
        var reader = await factory.SignUpAsync("Journal Keeper");
        var book = await CreateAsync(reader, new CreateBookRequest("The Waves", "Virginia Woolf", BookStatus.Finished, 5, StartedOn: new DateOnly(2024, 5, 1), FinishedOn: new DateOnly(2024, 6, 9)));
        await reader.PostAsJsonAsync($"/books/{book.Id}/sessions", new CreateSessionRequest(new DateOnly(2024, 5, 20), 10, 60, "Read on the train to the coast."), JsonOptions);
        var written = await reader.PostAsJsonAsync("/books/journal", new JournalEntryRequest(new DateOnly(2024, 6, 10), "Finished it by the sea; the last pages undid me.", book.Id), JsonOptions);
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/books/journal", new JournalEntryRequest(null, "  "), JsonOptions)).StatusCode);

        var year = (await reader.GetFromJsonAsync<JournalItem[]>("/books/journal?year=2024", JsonOptions))!;
        Assert.Equal([JournalKind.Entry, JournalKind.Finished, JournalKind.Session, JournalKind.Started], year.Select(item => item.Kind));
        Assert.Equal("The Waves", year[0].Title);
        Assert.Empty((await reader.GetFromJsonAsync<JournalItem[]>("/books/journal?year=2023", JsonOptions))!);
        Assert.Contains(2024, (await reader.GetFromJsonAsync<int[]>("/books/journal/years", JsonOptions))!);

        var found = (await reader.GetFromJsonAsync<JournalItem[]>("/books/journal?q=TRAIN", JsonOptions))!;
        Assert.Equal(JournalKind.Session, Assert.Single(found).Kind);

        var markdown = await reader.GetStringAsync("/books/journal/2024.md");
        Assert.Contains("# Reading journal, 2024", markdown);
        Assert.Contains("> Finished it by the sea", markdown);

        // Another reader sees none of it.
        var other = await factory.SignUpAsync("Journal Stranger");
        Assert.Empty((await other.GetFromJsonAsync<JournalItem[]>("/books/journal?year=2024", JsonOptions))!);
        var id = (await written.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/books/journal/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/journal/{id}")).StatusCode);
    }

    [Fact]
    public async Task Books_are_linked_as_editions_and_ideas_and_mapped()
    {
        var reader = await factory.SignUpAsync("Map Maker");
        var stranger = await factory.SignUpAsync("Map Stranger");
        var original = await CreateAsync(reader, new CreateBookRequest("Anna Karenina", "Leo Tolstoy", BookStatus.Want, null, Translator: "Constance Garnett"));
        var newer = await CreateAsync(reader, new CreateBookRequest("Anna Karenina", "Leo Tolstoy", BookStatus.Finished, 5, Translator: "Pevear and Volokhonsky"));
        var answer = await CreateAsync(reader, new CreateBookRequest("Madame Bovary", "Gustave Flaubert", BookStatus.Want, null));
        var theirs = await CreateAsync(stranger, new CreateBookRequest("Not Yours", "Someone", BookStatus.Want, null));

        Assert.Equal(HttpStatusCode.Created, (await reader.PostAsJsonAsync($"/books/{newer.Id}/links", new BookLinkRequest(original.Id, LinkKind.Edition), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync($"/books/{original.Id}/links", new BookLinkRequest(newer.Id, LinkKind.Edition), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await reader.PostAsJsonAsync($"/books/{answer.Id}/links", new BookLinkRequest(original.Id, LinkKind.Contrasts, "Two adulteries, two endings."), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync($"/books/{answer.Id}/links", new BookLinkRequest(answer.Id, LinkKind.Related), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.PostAsJsonAsync($"/books/{answer.Id}/links", new BookLinkRequest(theirs.Id, LinkKind.Related), JsonOptions)).StatusCode);

        var links = (await reader.GetFromJsonAsync<BookLinked[]>($"/books/{original.Id}/links", JsonOptions))!;
        Assert.Equal([(LinkKind.Edition, "Pevear and Volokhonsky", BookStatus.Finished), (LinkKind.Contrasts, null, BookStatus.Want)], links.Select(link => (link.Kind, link.Translator, link.Status)));

        // Two steps out from Madame Bovary reach the other edition, through the first.
        var map = (await reader.GetFromJsonAsync<LiteraryMap>($"/books/map?book={answer.Id}", JsonOptions))!;
        Assert.Equal([(answer.Id, 0), (original.Id, 1), (newer.Id, 2)], map.Books.Select(node => (node.Id, node.Depth)).OrderBy(node => node.Depth));
        Assert.Equal(2, map.Links.Length);
        Assert.Empty((await stranger.GetFromJsonAsync<LiteraryMap>("/books/map", JsonOptions))!.Books);

        // Deleting a book takes its links.
        await reader.DeleteAsync($"/books/{original.Id}");
        Assert.Empty((await reader.GetFromJsonAsync<BookLinked[]>($"/books/{newer.Id}/links", JsonOptions))!);
    }

    [Fact]
    public async Task A_collection_carries_notes_and_a_plan_that_says_whether_it_fits()
    {
        var reader = await factory.SignUpAsync("Planning Reader");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // A year's pace: 3,650 pages finished, ten a day.
        await CreateAsync(reader, new CreateBookRequest("Read Before", "Someone", BookStatus.Finished, null, Pages: 3650, FinishedOn: today.AddDays(-30)));
        var first = await CreateAsync(reader, new CreateBookRequest("Short One", "Someone", BookStatus.Want, null, Pages: 100));
        var second = await CreateAsync(reader, new CreateBookRequest("Long One", "Someone", BookStatus.Want, null, Pages: 400));
        var id = (await (await reader.PostAsJsonAsync("/books/collections", new CollectionRequest("Summer"), JsonOptions)).Content.ReadFromJsonAsync<CollectionResponse>(JsonOptions))!.Id;
        foreach (var book in new[] { first, second })
        {
            await reader.PostAsJsonAsync($"/books/collections/{id}/books", new CollectionBookRequest(book.Id), JsonOptions);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync($"/books/collections/{id}/books/{second.Id}", new CollectionNoteRequest("Start here: it sets up the rest."), JsonOptions)).StatusCode);
        var collection = (await reader.GetFromJsonAsync<CollectionResponse>($"/books/collections/{id}", JsonOptions))!;
        Assert.Equal(new string?[] { null, "Start here: it sets up the rest." }, collection.Notes);

        Assert.Equal(HttpStatusCode.NoContent, (await reader.GetAsync($"/books/collections/{id}/plan")).StatusCode);
        await reader.PutAsJsonAsync($"/books/collections/{id}/plan", new CollectionPlanRequest(today.AddDays(20)), JsonOptions);
        var plan = (await reader.GetFromJsonAsync<PlanEstimate>($"/books/collections/{id}/plan", JsonOptions))!;
        Assert.Equal((500, 10, 50, 20, false, 1), (plan.PagesLeft, plan.PagesPerDay, plan.DaysNeeded, plan.DaysLeft, plan.Fits, plan.LeaveOut));
        Assert.Equal(today.AddDays(50), plan.FitsBy);

        await reader.PutAsJsonAsync($"/books/collections/{id}/plan", new CollectionPlanRequest(today.AddDays(60)), JsonOptions);
        Assert.True((await reader.GetFromJsonAsync<PlanEstimate>($"/books/collections/{id}/plan", JsonOptions))!.Fits);

        // The note goes with the list when it is shared; the reader's own notes on the book never do.
        string token;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Planning Reader"));
            token = (await Collections.ShareAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), id))!;
        }

        Assert.Contains("Start here: it sets up the rest.", await factory.CreateClient().GetStringAsync($"/shared/{token}"));
    }

    [Fact]
    public async Task A_copy_keeps_its_history_and_pictures()
    {
        var reader = await factory.SignUpAsync("Collector Of Copies");
        var stranger = await factory.SignUpAsync("Curious About Copies");
        var book = await CreateAsync(reader, new CreateBookRequest("The Hobbit", "J. R. R. Tolkien", BookStatus.Finished, 5, Inscription: "To Ann, Christmas 1966", PreviousOwners: "Ann Reid; then her son", Signed: true));
        Assert.Equal(("Ann Reid; then her son", true), (book.PreviousOwners, book.Signed));

        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0]), "file", "plate.png" },
            { new StringContent("The bookplate"), "caption" },
        };
        var uploaded = await reader.PostAsync($"/books/{book.Id}/photos", form);
        // The upload answers the book page's form by going back to the page.
        Assert.Contains("photo=saved", uploaded.RequestMessage!.RequestUri!.ToString());
        var photo = Assert.Single((await reader.GetFromJsonAsync<CopyPhotoResponse[]>($"/books/{book.Id}/photos", JsonOptions))!);
        Assert.Equal("The bookplate", photo.Caption);
        Assert.Equal("image/png", (await reader.GetAsync($"/books/{book.Id}/photos/{photo.Id}")).Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/photos/{photo.Id}")).StatusCode);

        using var notPicture = new MultipartFormDataContent { { new ByteArrayContent("hello"u8.ToArray()), "file", "a.png" } };
        Assert.Contains("photo=unsupported", (await reader.PostAsync($"/books/{book.Id}/photos", notPicture)).RequestMessage!.RequestUri!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/{book.Id}/photos/{photo.Id}")).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<CopyPhotoResponse[]>($"/books/{book.Id}/photos", JsonOptions))!);
    }

    [Fact]
    public async Task A_club_meets_with_questions_kept_until_it_begins_and_spoilers_folded()
    {
        var host = await factory.SignUpAsync("Meeting Host");
        var guest = await factory.SignUpAsync("Meeting Guest");
        var outsider = await factory.SignUpAsync("Meeting Outsider");
        await host.PostAsJsonAsync("/books", new CreateBookRequest("Beloved", "Toni Morrison", BookStatus.Reading, null), JsonOptions);
        var club = (await (await host.PostAsJsonAsync("/books/clubs", new ClubRequest("First Mondays"), JsonOptions)).Content.ReadFromJsonAsync<ClubResponse>(JsonOptions))!;
        await host.PostAsJsonAsync($"/books/clubs/{club.Id}/members", new ClubInvite(await factory.ReaderIdAsync("Meeting Guest")), JsonOptions);
        await host.PostAsJsonAsync($"/books/clubs/{club.Id}/books", new ClubBookRequest(Title: "Beloved", Author: "Toni Morrison"), JsonOptions);
        var bookId = (await host.GetFromJsonAsync<ClubResponse>($"/books/clubs/{club.Id}", JsonOptions))!.Books.Single().Id;

        var soon = new ClubMeetingRequest(bookId, DateTimeOffset.UtcNow.AddDays(7), "Part One", "Sethe and 124.");
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync($"/books/clubs/{club.Id}/meetings", soon, JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await host.PostAsJsonAsync($"/books/clubs/{club.Id}/meetings", soon, JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await host.PostAsJsonAsync($"/books/clubs/{club.Id}/meetings", new ClubMeetingRequest(bookId, DateTimeOffset.UtcNow.AddDays(-7), "The first chapters"), JsonOptions)).StatusCode);
        var meetings = (await guest.GetFromJsonAsync<ClubMeetingResponse[]>($"/books/clubs/{club.Id}/meetings", JsonOptions))!;
        Assert.Equal(["Part One", "The first chapters"], meetings.Select(meeting => meeting.Through));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/books/clubs/{club.Id}/meetings")).StatusCode);

        // A question waits unseen by the other members until the meeting, though the host sees it to plan.
        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostAsJsonAsync($"/books/clubs/{club.Id}/meetings/{meetings[0].Id}/questions", new ClubQuestionRequest("Who is Beloved, really?"), JsonOptions)).StatusCode);
        Assert.Equal("Who is Beloved, really?", Assert.Single((await host.GetFromJsonAsync<ClubMeetingResponse[]>($"/books/clubs/{club.Id}/meetings", JsonOptions))![0].Questions).Text);
        var third = await factory.SignUpAsync("Meeting Third");
        await host.PostAsJsonAsync($"/books/clubs/{club.Id}/members", new ClubInvite(await factory.ReaderIdAsync("Meeting Third")), JsonOptions);
        var seenByThird = (await third.GetFromJsonAsync<ClubMeetingResponse[]>($"/books/clubs/{club.Id}/meetings", JsonOptions))![0];
        Assert.Equal((0, 1), (seenByThird.Questions.Length, seenByThird.Waiting));

        Assert.Equal(HttpStatusCode.NoContent, (await host.PutAsJsonAsync($"/books/clubs/{club.Id}/meetings/{meetings[1].Id}/summary", new ClubSummaryRequest("We agreed the opening is a ghost story."), JsonOptions)).StatusCode);
        Assert.Equal("We agreed the opening is a ghost story.", (await guest.GetFromJsonAsync<ClubMeetingResponse[]>($"/books/clubs/{club.Id}/meetings", JsonOptions))![1].Summary);

        await guest.PostAsJsonAsync($"/books/clubs/{club.Id}/books/{bookId}/posts", new ClubPostRequest("The ending!", Spoiler: true), JsonOptions);
        Assert.True(Assert.Single((await host.GetFromJsonAsync<ClubPostResponse[]>($"/books/clubs/{club.Id}/books/{bookId}/posts", JsonOptions))!).Spoiler);
    }

    [Fact]
    public async Task A_book_added_as_finished_on_an_earlier_day_gets_no_start_after_it()
    {
        var reader = await factory.SignUpAsync("Dated Reader");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var earlier = await CreateAsync(reader, new CreateBookRequest("Read Long Ago", "Someone", BookStatus.Finished, null, FinishedOn: new DateOnly(2015, 6, 1)));
        Assert.Equal(((DateOnly?)null, (DateOnly?)new DateOnly(2015, 6, 1)), (earlier.StartedOn, earlier.FinishedOn));
        var now = await CreateAsync(reader, new CreateBookRequest("Read Today", "Someone", BookStatus.Finished, null));
        Assert.Equal(((DateOnly?)today, (DateOnly?)today), (now.StartedOn, now.FinishedOn));
    }

    [Fact]
    public async Task Goals_of_a_readers_own_count_the_years_finishes()
    {
        var reader = await factory.SignUpAsync("Goal Setter");
        var year = DateTime.UtcNow.Year;
        await CreateAsync(reader, new CreateBookRequest("Old Favourite", "Known Author", BookStatus.Finished, null, FinishedOn: new DateOnly(year - 2, 3, 1)));
        await CreateAsync(reader, new CreateBookRequest("The Histories", "Herodotus", BookStatus.Finished, null, FinishedOn: new DateOnly(year, 1, 5), Tags: ["history"]));
        await CreateAsync(reader, new CreateBookRequest("SPQR", "Mary Beard", BookStatus.Finished, null, FinishedOn: new DateOnly(year, 1, 9), Tags: ["History"], Series: "Rome"));
        await CreateAsync(reader, new CreateBookRequest("Another By Them", "Known Author", BookStatus.Finished, null, FinishedOn: new DateOnly(year, 1, 12)));

        foreach (var goal in new[] { new ReadingGoalRequest(GoalKind.Tag, "History", 3), new ReadingGoalRequest(GoalKind.Author, "known author", 1), new ReadingGoalRequest(GoalKind.Series, "rome", 2), new ReadingGoalRequest(GoalKind.NewAuthors, null, 2) })
        {
            Assert.Equal(HttpStatusCode.Created, (await reader.PostAsJsonAsync("/books/goals", goal, JsonOptions)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/books/goals", new ReadingGoalRequest(GoalKind.Tag, " ", 3), JsonOptions)).StatusCode);
        var progress = (await reader.GetFromJsonAsync<GoalProgress[]>("/books/goals", JsonOptions))!;
        Assert.Equal([2, 1, 1, 2], progress.Select(goal => goal.Done));
        Assert.Equal(["The Histories", "SPQR"], progress[0].Books);
        Assert.Equal(["Herodotus", "Mary Beard"], progress[3].Books);
        Assert.Equal("3 books tagged history", progress[0].Label);
        Assert.Null(progress[1].Pace);
        Assert.Contains("1 to go", ReadingGoals.Pace(1, year, new DateOnly(year, 1, 1)));
        Assert.Contains("about one every", ReadingGoals.Pace(4, year, new DateOnly(year, 1, 1)));
        Assert.Null(ReadingGoals.Pace(4, year - 1, new DateOnly(year, 1, 1)));

        var other = await factory.SignUpAsync("Goal Stranger");
        Assert.Empty((await other.GetFromJsonAsync<GoalProgress[]>("/books/goals", JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/books/goals/{progress[0].Id}")).StatusCode);
        Assert.Equal(4, (await reader.GetFromJsonAsync<LibraryExport>("/books/export", JsonOptions))!.Goals!.Length);
    }

    [Fact]
    public void A_meeting_time_is_the_moment_meant_in_the_zone_it_was_typed_in()
    {
        var paris = ClubMeetings.FromLocal(new DateTime(2026, 11, 2, 19, 0, 0), "Europe/Paris");
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero), paris);
        var summer = ClubMeetings.FromLocal(new DateTime(2026, 7, 2, 19, 0, 0), "America/New_York");
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 23, 0, 0, TimeSpan.Zero), summer);
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 19, 0, 0, TimeSpan.Zero), ClubMeetings.FromLocal(new DateTime(2026, 7, 2, 19, 0, 0), "Not/AZone"));
        Assert.Contains("(Europe/Paris)", ClubMeetings.Say(paris, "Europe/Paris"));
        // Seven in the evening in Paris, written as the culture the tests run in writes a time.
        Assert.Contains(new DateTime(2026, 11, 2, 19, 0, 0).ToString("t", System.Globalization.CultureInfo.CurrentCulture), ClubMeetings.Say(paris, "Europe/Paris", nameZone: false));
    }

    [Fact]
    public async Task The_event_feed_keeps_what_webhooks_would_send_and_tokens_keep_to_their_areas()
    {
        var reader = await factory.SignUpAsync("Feed Reader");
        var stranger = await factory.SignUpAsync("Feed Stranger");
        var book = await CreateAsync(reader, new CreateBookRequest("Pale Fire", "Vladimir Nabokov", BookStatus.Want, null));
        await reader.PutAsJsonAsync($"/books/{book.Id}", ToUpdate(BookWrite.From(book) with { Status = BookStatus.Finished }), JsonOptions);
        await reader.PostAsync($"/books/{book.Id}/again", null);
        await WaitIdleAsync();

        var page = (await reader.GetFromJsonAsync<EventPage>("/books/events", JsonOptions))!;
        Assert.Equal(["book.added", "book.finished", "book.reread"], page.Events.Select(item => item.Event.GetProperty("event").GetString()));
        Assert.Equal("Pale Fire", page.Events[0].Event.GetProperty("book").GetProperty("title").GetString());
        Assert.Equal(page.Events[^1].Id, page.Next);
        Assert.Empty((await reader.GetFromJsonAsync<EventPage>($"/books/events?after={page.Next}", JsonOptions))!.Events);
        var two = (await reader.GetFromJsonAsync<EventPage>("/books/events?take=2", JsonOptions))!;
        Assert.Equal((2, true), (two.Events.Length, two.More));
        Assert.Single((await reader.GetFromJsonAsync<EventPage>("/books/events?kind=book.reread", JsonOptions))!.Events);
        Assert.Empty((await stranger.GetFromJsonAsync<EventPage>("/books/events", JsonOptions))!.Events);

        // A token kept to the journal and the feed opens those and nothing else.
        var made = (await (await reader.PostAsJsonAsync("/account/tokens", new NewApiTokenRequest("Journal script", true, ["journal", "events", "nonsense"]), JsonOptions))
            .Content.ReadFromJsonAsync<NewApiToken>(JsonOptions))!;
        Assert.Equal(["events", "journal"], made.Areas!);
        var script = factory.CreateClient();
        script.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", made.Token);
        Assert.Equal(HttpStatusCode.Created, (await script.PostAsJsonAsync("/books/journal", new JournalEntryRequest(null, "Written by a script."), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/books/events")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/version")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.GetAsync("/books")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.GetAsync($"/books/{book.Id}/photos")).StatusCode);
        Assert.Equal(ApiTokens.Files, ApiTokens.AreaOf("/books/12/ebook"));
        Assert.Equal(ApiTokens.Library, ApiTokens.AreaOf("/books/12/sessions"));
    }

    private async Task WaitIdleAsync()
    {
        var events = factory.Services.GetRequiredService<ShelfEvents>();
        for (var i = 0; i < 100 && !events.Idle; i++)
        {
            await Task.Delay(50);
        }
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

    // A long year of reading opens a page at a time, the latest first, with the way to the rest: drawn whole, tens
    // of thousands of lines took minutes.
    [Fact]
    public async Task A_long_year_of_reading_opens_a_page_at_a_time()
    {
        var reader = await factory.SignUpAsync("Long Year Reader");
        var readerId = await factory.ReaderIdAsync("Long Year Reader");
        var book = await CreateAsync(reader, new CreateBookRequest("Read All Year", "Someone", BookStatus.Reading, null));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            db.Sessions.AddRange(Enumerable.Range(0, 350).Select(n => new ReadingSession { BookId = book.Id, Date = new DateOnly(today.Year, 1, 1).AddDays(n % 300), FromPage = n, ToPage = n + 1 }));
            await db.SaveChangesAsync();
        }

        var journal = await reader.GetStringAsync("/journal");
        Assert.Equal(300, journal.Split("class=\"journal-item").Length - 1);
        // 350 sessions, and the day the book was begun.
        Assert.Contains("Show 51 more", journal);
        Assert.Contains("Show all 351", journal);
    }

    // A list of ids in a query goes as one parameter, whatever its length: past SQLite's 32,766 parameters, one each
    // failed, and tens of thousands took over a minute to build.
    [Fact]
    public async Task A_query_by_tens_of_thousands_of_ids_runs()
    {
        await factory.SignUpAsync("Many Ids Reader");
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(await factory.ReaderIdAsync("Many Ids Reader"));
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var ids = Enumerable.Range(1, 40_000).ToList();
        Assert.Equal(0, await db.Books.CountAsync(book => ids.Contains(book.Id)));
    }

    // The long lists open light, with the way to the rest: quotes and series a page at a time, the open books and
    // each year's finishes up to a limit, past which the library and the year's page hold them all.
    [Fact]
    public async Task Long_lists_open_a_page_at_a_time()
    {
        var reader = await factory.SignUpAsync("Long Lists Reader");
        var readerId = await factory.ReaderIdAsync("Long Lists Reader");
        var quoted = await CreateAsync(reader, new CreateBookRequest("Much Quoted", "Someone", BookStatus.Want, null));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(readerId);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            db.Quotes.AddRange(Enumerable.Range(0, 210).Select(n => new Quote { BookId = quoted.Id, Text = $"Line {n}.", NotedAt = DateTimeOffset.UtcNow.AddMinutes(-n) }));
            db.Books.AddRange(Enumerable.Range(0, 101).Select(n => new Book { OwnerId = readerId, Title = $"Series Book {n}", Author = "Someone", Series = $"Series {n}", SeriesNumber = 1, Status = BookStatus.Want, AddedAt = DateTimeOffset.UtcNow }));
            db.Books.AddRange(Enumerable.Range(0, 25).Select(n => new Book { OwnerId = readerId, Title = $"Open Book {n}", Author = "Someone", Status = BookStatus.Reading, AddedAt = DateTimeOffset.UtcNow }));
            db.Books.AddRange(Enumerable.Range(0, 61).Select(n => new Book { OwnerId = readerId, Title = $"Done Book {n}", Author = "Someone", Status = BookStatus.Finished, FinishedOn = new DateOnly(2021, 1, 1).AddDays(n), AddedAt = DateTimeOffset.UtcNow }));
            await db.SaveChangesAsync();
        }

        var quotes = await reader.GetStringAsync("/quotes");
        Assert.Equal(200, quotes.Split("<blockquote>").Length - 1);
        Assert.Contains("Show all 210", quotes);
        var series = await reader.GetStringAsync("/series");
        Assert.Contains("Show 1 more", series);
        Assert.Contains("Show all 101", series);
        Assert.Contains("All 25 you are reading", await reader.GetStringAsync("/reading"));
        var years = WebUtility.HtmlDecode(await reader.GetStringAsync("/years"));
        Assert.Contains("and 1 more", years);
        Assert.Contains("href=\"/years/2021\"", years);
    }
}
