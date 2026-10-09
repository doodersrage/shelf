using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public static class BookEndpoints
{
    public static RouteGroupBuilder MapBooks(this IEndpointRouteBuilder app)
    {
        var books = app.MapGroup("/books").WithTags("Books").RequireAuthorization();

        books.MapGet("/", ListBooks);
        books.MapGet("/stats", GetStats).WithTags("Shelf");
        books.MapGet("/export", ExportLibrary).WithTags("Shelf");
        books.MapPost("/import", ImportLibrary).WithTags("Shelf");
        books.MapGet("/export/full", ExportEverything).WithTags("Shelf");
        books.MapPost("/import/full", ImportEverything).DisableAntiforgery().WithTags("Shelf");
        books.MapGet("/reminders", Asking.Remind).WithTags("Lending");
        books.MapGet("/shelves", Asking.Shelves).WithTags("Lending");
        books.MapGet("/shelves/{id:int}", Asking.Shelf).WithTags("Lending");
        books.MapPut("/shelves/open", Asking.OpenShelf).WithTags("Lending");
        books.MapGet("/asks", Asking.Asks).WithTags("Lending");
        books.MapPost("/asks/{id:int}/lend", Asking.LendAsk).WithTags("Lending");
        books.MapDelete("/asks/{id:int}", Asking.DropAsk).WithTags("Lending");
        books.MapGet("/lookup", LookupBook);
        books.MapGet("/pick", PickBook);
        books.MapGet("/authors", ListAuthors);
        books.MapGet("/series", ListSeries);
        books.MapGet("/places", ListPlaces);
        books.MapGet("/recommenders", ListRecommenders);
        books.MapGet("/quotes", ListAllQuotes);
        books.MapGet("/calendar", GetCalendar);
        books.MapGet("/years", ListYears);
        books.MapGet("/loans", ListLoans);
        books.MapGet("/copies", ListCopies);
        books.MapGet("/{id:int}", GetBook);
        books.MapPost("/", CreateBook);
        books.MapPut("/{id:int}", UpdateBook);
        books.MapDelete("/{id:int}", DeleteBook);
        books.MapGet("/{id:int}/quotes", ListQuotes);
        books.MapPost("/{id:int}/quotes", CreateQuote);
        books.MapDelete("/{id:int}/quotes/{quoteId:int}", DeleteQuote);
        books.MapPost("/{id:int}/sessions", CreateSession);
        books.MapDelete("/{id:int}/sessions/{sessionId:int}", DeleteSession);
        books.MapPost("/{id:int}/return", ReturnBook);
        books.MapPost("/{id:int}/lend", Lending.Lend).WithTags("Lending");
        books.MapPost("/{id:int}/ask", Asking.Ask).WithTags("Lending");
        books.MapGet("/borrowed", Lending.Borrowed).WithTags("Lending");
        books.MapPost("/borrowed/{id:int}/return", Lending.GiveBack).WithTags("Lending");
        books.MapPost("/{id:int}/enrich", EnrichBook);
        books.MapGet("/{id:int}/highlights", EbookEndpoints.ListHighlights);
        books.MapPost("/{id:int}/highlights", EbookEndpoints.CreateHighlight);
        books.MapPut("/{id:int}/highlights/{highlightId:int}", EbookEndpoints.UpdateHighlight);
        books.MapDelete("/{id:int}/highlights/{highlightId:int}", EbookEndpoints.DeleteHighlight);
        books.MapPost("/{id:int}/ebook", EbookEndpoints.Upload).DisableAntiforgery();
        books.MapDelete("/{id:int}/ebook", EbookEndpoints.Remove);
        books.MapGet("/{id:int}/ebook/file", EbookEndpoints.File);
        books.MapGet("/{id:int}/ebook/chapters/{index:int}", EbookEndpoints.Chapter);
        books.MapGet("/{id:int}/ebook/assets/{*path}", EbookEndpoints.Asset);
        books.MapPost("/{id:int}/audio", AudioEndpoints.Upload).DisableAntiforgery();
        books.MapDelete("/{id:int}/audio", AudioEndpoints.Remove);
        books.MapGet("/{id:int}/audio/tracks/{index:int}", AudioEndpoints.Track);

        // Another shelf reaches these with a device key; the rest of the API needs a signed-in reader.
        var sync = app.MapGroup("/books/sync").WithTags("Shelf").RequireAuthorization(SyncEndpoints.Policy);
        sync.MapGet("/", SyncEndpoints.Catalog);
        sync.MapPost("/books", SyncEndpoints.Ensure);
        sync.MapGet("/{key}/ebook", SyncEndpoints.Ebook);
        sync.MapPost("/{key}/ebook", SyncEndpoints.UploadEbook).DisableAntiforgery();
        sync.MapGet("/{key}/audio", SyncEndpoints.Audio);
        sync.MapPost("/{key}/audio", SyncEndpoints.UploadAudio).DisableAntiforgery();
        sync.MapPut("/{key}/progress", SyncEndpoints.Progress);

        app.MapGet("/settings", GetSettings).WithTags("Shelf").RequireAuthorization();
        app.MapPut("/settings", UpdateSettings).WithTags("Shelf").RequireAuthorization();

        return books;
    }

    private static async Task<Ok<BookResponse[]>> ListBooks(
        ShelfDb db,
        CancellationToken cancellationToken,
        string? q = null,
        BookStatus? status = null,
        string? tag = null,
        string? author = null,
        bool? loved = null,
        bool? loaned = null,
        BookFormat? format = null,
        string? series = null,
        string? place = null,
        string? recommendedBy = null,
        string? loanedTo = null,
        string? sort = null)
    {
        var books = await BookRules.Filtered(db.Books, q, status, tag, author, loved, loaned, format, series, place, recommendedBy, loanedTo)
            .AsNoTracking()
            .WithDetails()
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(BookRules.Sort(books, sort).Select(BookResponse.From).ToArray());
    }

    private static async Task<Ok<ShelfStatsResponse>> GetStats(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await BookRules.SummarizeAsync(db, cancellationToken));

    private static async Task<Results<Ok<BookResponse>, NotFound>> GetBook(
        int id,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.AsNoTracking().WithDetails().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        return book is null ? TypedResults.NotFound() : TypedResults.Ok(BookResponse.From(book));
    }

    private static async Task<Results<Created<BookResponse>, ValidationProblem>> CreateBook(
        CreateBookRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var write = request.ToWrite();
        var problems = BookRules.Validate(write);
        if (problems is not null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        var book = new Book
        {
            Title = write.Title.Trim(),
            Author = write.Author.Trim(),
        };
        BookRules.Apply(book, write);
        db.Books.Add(book);
        await BookRules.SyncTagsAsync(db, book, write.Tags, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        return TypedResults.Created($"/books/{book.Id}", BookResponse.From(book));
    }

    private static async Task<Results<Ok<BookResponse>, NotFound, ValidationProblem>> UpdateBook(
        int id,
        UpdateBookRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var write = request.ToWrite();
        var problems = BookRules.Validate(write);
        if (problems is not null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        var book = await db.Books.Include(existing => existing.Tags).FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        BookRules.Apply(book, write);
        await BookRules.SyncTagsAsync(db, book, write.Tags, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        await db.Entry(book).Collection(existing => existing.Quotes).LoadAsync(cancellationToken);
        await db.Entry(book).Collection(existing => existing.Sessions).LoadAsync(cancellationToken);
        return TypedResults.Ok(BookResponse.From(book));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteBook(
        int id,
        ShelfDb db,
        EbookStore store,
        AudioStore audio,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        store.Delete(book.EbookStoredName);
        audio.Delete(book.AudioStoredName);
        db.Books.Remove(book);
        await db.SaveChangesAsync(cancellationToken);
        await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<QuoteResponse[]>, NotFound>> ListQuotes(
        int id,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var exists = await db.Books.AnyAsync(book => book.Id == id, cancellationToken);
        if (!exists)
        {
            return TypedResults.NotFound();
        }

        var quotes = await db.Quotes.AsNoTracking()
            .Where(quote => quote.BookId == id)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(quotes
            .OrderBy(quote => quote.NotedAt)
            .ThenBy(quote => quote.Id)
            .Select(QuoteResponse.From)
            .ToArray());
    }

    private static async Task<Results<Created<QuoteResponse>, NotFound, ValidationProblem>> CreateQuote(
        int id,
        CreateQuoteRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(existing => existing.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        var problems = BookRules.ValidateQuote(request.Text, request.Page, book.Pages);
        if (problems is not null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        var quote = new Quote
        {
            BookId = book.Id,
            Text = request.Text.Trim(),
            Page = request.Page,
            NotedAt = DateTimeOffset.UtcNow,
        };
        db.Quotes.Add(quote);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/books/{book.Id}/quotes/{quote.Id}", QuoteResponse.From(quote));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteQuote(
        int id,
        int quoteId,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var quote = await db.Quotes.FirstOrDefaultAsync(
            existing => existing.Id == quoteId && existing.BookId == id,
            cancellationToken);
        if (quote is null)
        {
            return TypedResults.NotFound();
        }

        db.Quotes.Remove(quote);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<LibraryExport>> ExportLibrary(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await Backup.ExportAsync(db, cancellationToken));

    private static async Task<TempFileResult> ExportEverything(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CancellationToken cancellationToken)
    {
        var path = TempFileResult.NewPath(".zip");
        await Backup.WriteShelfAsync(db, ebooks, audio, path, cancellationToken);
        return new TempFileResult(path, "application/zip", $"shelf-backup-{DateTime.UtcNow:yyyy-MM-dd}.zip");
    }

    // Like the e-book upload, this answers the form on the Backup page with a redirect back to it.
    private static async Task<RedirectHttpResult> ImportEverything(
        IFormFile? file,
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return TypedResults.Redirect("/backup?restore=unreadable");
        }

        var path = TempFileResult.NewPath(".zip");
        try
        {
            await using (var output = File.Create(path))
            {
                await file.CopyToAsync(output, cancellationToken);
            }

            RestoreResult? result;
            try
            {
                result = await Backup.RestoreShelfAsync(db, ebooks, audio, path, cancellationToken);
            }
            catch (InvalidDataException)
            {
                result = null;
            }

            return TypedResults.Redirect(result is null
                ? "/backup?restore=unreadable"
                : $"/backup?restore=done&added={result.Added}&skipped={result.Skipped}&files={result.Files}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<Results<Ok<ImportResult>, ValidationProblem>> ImportLibrary(
        LibraryExport export,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        if (export.Books is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Books"] = ["A backup needs a books list."],
            });
        }

        if (export.YearlyGoal is < 0 or > 1000)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["YearlyGoal"] = ["The yearly goal must be between 0 and 1000."],
            });
        }

        return TypedResults.Ok(await BookRules.ImportAsync(db, export, cancellationToken));
    }

    private static async Task<Ok<QuoteListItem[]>> ListAllQuotes(
        ShelfDb db,
        CancellationToken cancellationToken,
        string? q = null)
    {
        var quotes = await db.Quotes.AsNoTracking()
            .Join(
                db.Books.AsNoTracking(),
                quote => quote.BookId,
                book => book.Id,
                (quote, book) => new QuoteListItem(quote.Id, book.Id, book.Title, book.Author, quote.Text, quote.Page, quote.NotedAt))
            .ToListAsync(cancellationToken);

        var ordered = quotes.OrderByDescending(quote => quote.NotedAt).ThenByDescending(quote => quote.Id);
        return TypedResults.Ok(BookRules.MatchingQuotes(ordered, q).ToArray());
    }

    private static async Task<Results<Created<ReadingSessionResponse>, NotFound, ValidationProblem>> CreateSession(
        int id,
        CreateSessionRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(existing => existing.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        var problems = BookRules.ValidateSession(request, book.Pages);
        if (problems is not null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        var session = new ReadingSession
        {
            BookId = book.Id,
            Date = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow),
            FromPage = request.FromPage,
            ToPage = request.ToPage,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
        };
        db.Sessions.Add(session);
        BookRules.AdvanceProgress(book, session.ToPage);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/books/{book.Id}/sessions/{session.Id}", ReadingSessionResponse.From(session));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteSession(
        int id,
        int sessionId,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(
            existing => existing.Id == sessionId && existing.BookId == id,
            cancellationToken);
        if (session is null)
        {
            return TypedResults.NotFound();
        }

        db.Sessions.Remove(session);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<ShelfSettingsResponse>> GetSettings(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(new ShelfSettingsResponse(await BookRules.GetGoalAsync(db, cancellationToken)));

    private static async Task<Results<Ok<ShelfSettingsResponse>, ValidationProblem>> UpdateSettings(
        UpdateSettingsRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        await BookRules.SetGoalAsync(db, request.YearlyGoal, cancellationToken);
        return TypedResults.Ok(new ShelfSettingsResponse(request.YearlyGoal));
    }

    private static async Task<Results<Ok<CatalogMatch>, NotFound>> LookupBook(
        IBookLookup lookup,
        CancellationToken cancellationToken,
        string? isbn = null,
        string? title = null,
        string? author = null)
    {
        var match = await lookup.FindAsync(isbn, title, author, cancellationToken);
        return match is null ? TypedResults.NotFound() : TypedResults.Ok(match);
    }

    private static async Task<Results<Ok<BookResponse>, NotFound>> EnrichBook(
        int id,
        ShelfDb db,
        IBookLookup lookup,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.WithDetails().FirstOrDefaultAsync(existing => existing.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        var match = await lookup.FindAsync(book.Isbn, book.Title, book.Author, cancellationToken);
        if (match is not null)
        {
            var tags = book.Tags.Select(tag => tag.Name).ToList();
            if (BookRules.FillEmpty(book, match, tags))
            {
                await BookRules.SyncTagsAsync(db, book, tags, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
            }
        }

        return TypedResults.Ok(BookResponse.From(book));
    }

    private static async Task<Results<Ok<BookResponse>, NotFound>> ReturnBook(
        int id,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.WithDetails().FirstOrDefaultAsync(existing => existing.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        BookRules.ReturnLoan(book);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(BookResponse.From(book));
    }

    private static async Task<Results<Ok<BookResponse>, NotFound>> PickBook(
        ShelfDb db,
        CancellationToken cancellationToken,
        BookStatus? status = null)
    {
        var chosen = status ?? BookStatus.Want;
        var books = await db.Books.AsNoTracking()
            .WithDetails()
            .Where(book => book.Status == chosen)
            .OrderBy(book => book.Id)
            .ToListAsync(cancellationToken);
        var pick = BookRules.Pick(books, chosen, DateOnly.FromDateTime(DateTime.UtcNow));
        return pick is null ? TypedResults.NotFound() : TypedResults.Ok(BookResponse.From(pick));
    }

    private static async Task<Ok<AuthorCount[]>> ListAuthors(ShelfDb db, CancellationToken cancellationToken)
    {
        var authors = await db.Books.AsNoTracking().Select(book => book.Author).ToListAsync(cancellationToken);
        return TypedResults.Ok(BookRules.AuthorCounts(authors));
    }

    private static async Task<Ok<SeriesShelf[]>> ListSeries(ShelfDb db, CancellationToken cancellationToken)
    {
        var books = await db.Books.AsNoTracking()
            .Where(book => book.Series != null)
            .Select(book => new Book
            {
                Title = book.Title,
                Author = book.Author,
                Id = book.Id,
                Series = book.Series,
                SeriesNumber = book.SeriesNumber,
                Status = book.Status,
            })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(BookRules.SeriesShelves(books));
    }

    private static async Task<Ok<PlaceCount[]>> ListPlaces(ShelfDb db, CancellationToken cancellationToken)
    {
        var locations = await db.Books.AsNoTracking().Select(book => book.Location).ToListAsync(cancellationToken);
        return TypedResults.Ok(BookRules.Places(locations));
    }

    private static async Task<Results<Ok<ReadingMonth>, BadRequest>> GetCalendar(
        ShelfDb db,
        CancellationToken cancellationToken,
        int? year = null,
        int? month = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var chosenYear = year ?? today.Year;
        var chosenMonth = month ?? today.Month;
        if (chosenMonth is < 1 or > 12 || chosenYear is < 1 or > 9999)
        {
            return TypedResults.BadRequest();
        }

        var dates = await db.Sessions.AsNoTracking().Select(session => session.Date).ToListAsync(cancellationToken);
        return TypedResults.Ok(new ReadingMonth(chosenYear, chosenMonth, BookRules.ReadingDays(dates, chosenYear, chosenMonth)));
    }

    private static async Task<Ok<LoanCount[]>> ListLoans(ShelfDb db, CancellationToken cancellationToken)
    {
        var loans = await db.Books.AsNoTracking()
            .Select(book => new { book.LoanedTo, book.DueOn })
            .ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return TypedResults.Ok(BookRules.Loans(loans.Select(loan => (loan.LoanedTo, loan.DueOn)), today));
    }

    private static async Task<Ok<ConditionGroup[]>> ListCopies(ShelfDb db, CancellationToken cancellationToken)
    {
        var books = await db.Books.AsNoTracking()
            .Where(book => book.Condition != null)
            .Select(book => new Book
            {
                Id = book.Id,
                Title = book.Title,
                Author = book.Author,
                Condition = book.Condition,
                Acquisition = book.Acquisition,
            })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(BookRules.ByCondition(books));
    }

    private static async Task<Ok<FinishedYear[]>> ListYears(ShelfDb db, CancellationToken cancellationToken)
    {
        var books = await db.Books.AsNoTracking()
            .Where(book => book.Status == BookStatus.Finished)
            .Select(book => new Book
            {
                Id = book.Id,
                Title = book.Title,
                Author = book.Author,
                Status = book.Status,
                FinishedOn = book.FinishedOn,
            })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(BookRules.FinishedByYear(books));
    }

    private static async Task<Ok<RecommenderCount[]>> ListRecommenders(ShelfDb db, CancellationToken cancellationToken)
    {
        var names = await db.Books.AsNoTracking().Select(book => book.RecommendedBy).ToListAsync(cancellationToken);
        return TypedResults.Ok(BookRules.Recommenders(names));
    }
}
