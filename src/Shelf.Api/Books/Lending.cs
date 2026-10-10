using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Books;

public enum LendResult
{
    Lent,
    NoBook,
    NoReader,
    ToSelf,
    AlreadyOut,
}

// Lending to another reader of this shelf: the book stays on the owner's shelf, and shows on the borrower's loans.
public sealed record OpenBook(Book Book, bool Borrowed);

public sealed record PlaceRequest(int? EbookChapter);

public sealed record PlaceResponse(int? EbookChapter, int? AudioTrack, int? AudioSeconds);

public static class Lending
{
    public static async Task<LendResult> LendAsync(
        ShelfDb db,
        int bookId,
        int readerId,
        DateOnly? dueOn,
        CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (book is null)
        {
            return LendResult.NoBook;
        }

        if (readerId == db.ReaderId)
        {
            return LendResult.ToSelf;
        }

        var borrower = await db.Readers.AsNoTracking().FirstOrDefaultAsync(reader => reader.Id == readerId, cancellationToken);
        if (borrower is null)
        {
            return LendResult.NoReader;
        }

        if (book.LoanedTo is not null && book.BorrowerId != readerId)
        {
            return LendResult.AlreadyOut;
        }

        if (book.BorrowerId != readerId)
        {
            book.LoanedOn = DateOnly.FromDateTime(DateTime.UtcNow);
        }

        book.BorrowerId = borrower.Id;
        book.LoanedTo = borrower.Name;
        book.DueOn = dueOn;
        await db.SaveChangesAsync(cancellationToken);
        return LendResult.Lent;
    }

    public static async Task<BorrowedBook[]> BorrowedAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var readerId = db.ReaderId;
        var books = await db.Books.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(book => readerId != 0 && book.BorrowerId == readerId)
            .Select(book => new BorrowedBook(
                book.Id,
                book.Title,
                book.Author,
                Covers.For(book.Id, book.CoverUrl, book.FileCover),
                book.Owner == null ? "" : book.Owner.Name,
                book.LoanedOn,
                book.DueOn,
                book.EbookStoredName != null,
                book.AudioStoredName != null))
            .ToListAsync(cancellationToken);
        return books
            .OrderBy(book => book.DueOn ?? DateOnly.MaxValue)
            .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<bool> GiveBackAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        var readerId = db.ReaderId;
        var book = await db.Books.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => readerId != 0 && item.Id == bookId && item.BorrowerId == readerId, cancellationToken);
        if (book is null)
        {
            return false;
        }

        BookRules.ReturnLoan(book);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // A book the signed-in reader may open: one of their own, or one another reader has lent them.
    public static async Task<OpenBook?> OpenAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        var own = await db.Books.AsNoTracking().FirstOrDefaultAsync(book => book.Id == bookId, cancellationToken);
        if (own is not null)
        {
            return new OpenBook(own, Borrowed: false);
        }

        var readerId = db.ReaderId;
        var lent = await db.Books.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(book => readerId != 0 && book.Id == bookId && book.BorrowerId == readerId, cancellationToken);
        return lent is null ? null : new OpenBook(lent, Borrowed: true);
    }

    // The owner's marks, or the borrower's own marks on a book lent to them. Neither sees the other's.
    public static IQueryable<Highlight> Marks(ShelfDb db, OpenBook open)
    {
        var bookId = open.Book.Id;
        if (!open.Borrowed)
        {
            return db.Highlights.Where(mark => mark.BookId == bookId);
        }

        var readerId = db.ReaderId;
        return db.Highlights.IgnoreQueryFilters()
            .Where(mark => mark.BookId == bookId && mark.ReaderId == readerId && mark.Book!.BorrowerId == readerId);
    }

    // The owner's bookmarks, or the borrower's own on a book lent to them.
    public static IQueryable<AudioBookmark> Bookmarks(ShelfDb db, OpenBook open)
    {
        var bookId = open.Book.Id;
        if (!open.Borrowed)
        {
            return db.AudioBookmarks.Where(mark => mark.BookId == bookId);
        }

        var readerId = db.ReaderId;
        return db.AudioBookmarks.IgnoreQueryFilters()
            .Where(mark => mark.BookId == bookId && mark.ReaderId == readerId && mark.Book!.BorrowerId == readerId);
    }

    public static Highlight NewMark(ShelfDb db, OpenBook open, int chapterIndex, string text, string? note, string? prefix, string? suffix) => new()
    {
        BookId = open.Book.Id,
        ReaderId = open.Borrowed ? db.ReaderId : null,
        ChapterIndex = chapterIndex,
        Text = text,
        Note = note,
        Prefix = prefix,
        Suffix = suffix,
        NotedAt = DateTimeOffset.UtcNow,
    };

    // Where this reader stopped: the book's own place for the owner, a separate place for a borrower.
    public static async Task<LoanPlace> PlaceAsync(ShelfDb db, OpenBook open, CancellationToken cancellationToken = default)
    {
        if (!open.Borrowed)
        {
            return new LoanPlace
            {
                BookId = open.Book.Id,
                EbookChapter = open.Book.EbookChapter,
                AudioTrack = open.Book.AudioTrack,
                AudioSeconds = open.Book.AudioSeconds,
            };
        }

        var readerId = db.ReaderId;
        return await db.LoanPlaces.AsNoTracking()
                .FirstOrDefaultAsync(place => place.BookId == open.Book.Id && place.ReaderId == readerId, cancellationToken)
            ?? new LoanPlace { BookId = open.Book.Id, ReaderId = readerId };
    }

    public static async Task KeepPlaceAsync(
        ShelfDb db,
        OpenBook open,
        Action<LoanPlace> move,
        CancellationToken cancellationToken = default)
    {
        if (!open.Borrowed)
        {
            var book = await db.Books.FirstOrDefaultAsync(item => item.Id == open.Book.Id, cancellationToken);
            if (book is null)
            {
                return;
            }

            var place = new LoanPlace { EbookChapter = book.EbookChapter, AudioTrack = book.AudioTrack, AudioSeconds = book.AudioSeconds };
            move(place);
            book.EbookChapter = place.EbookChapter;
            book.AudioTrack = place.AudioTrack;
            book.AudioSeconds = place.AudioSeconds;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var readerId = db.ReaderId;
        if (!await db.Books.IgnoreQueryFilters().AnyAsync(book => book.Id == open.Book.Id && book.BorrowerId == readerId, cancellationToken))
        {
            return;
        }

        var kept = await db.LoanPlaces.FirstOrDefaultAsync(item => item.BookId == open.Book.Id && item.ReaderId == readerId, cancellationToken);
        if (kept is null)
        {
            kept = new LoanPlace { BookId = open.Book.Id, ReaderId = readerId };
            db.LoanPlaces.Add(kept);
        }

        move(kept);
        await db.SaveChangesAsync(cancellationToken);
    }

    // Where this reader stopped: the book's own place for the owner, their own for a borrower.
    public static async Task<Results<Ok<PlaceResponse>, NotFound>> Place(int id, ShelfDb db, CancellationToken cancellationToken)
    {
        if (await OpenAsync(db, id, cancellationToken) is not { } open)
        {
            return TypedResults.NotFound();
        }

        var place = await PlaceAsync(db, open, cancellationToken);
        return TypedResults.Ok(new PlaceResponse(place.EbookChapter, place.AudioTrack, place.AudioSeconds));
    }

    // A place read elsewhere, offline say, moves this reader's place only forward, as sync does.
    public static async Task<Results<NoContent, NotFound, ValidationProblem>> KeepPlace(
        int id,
        PlaceRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        if (request.EbookChapter is not int chapter || chapter < 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.EbookChapter)] = ["Give a chapter or page from 0 on."] });
        }

        if (await OpenAsync(db, id, cancellationToken) is not { } open)
        {
            return TypedResults.NotFound();
        }

        await KeepPlaceAsync(db, open, place =>
        {
            if (chapter > (place.EbookChapter ?? -1))
            {
                place.EbookChapter = chapter;
            }
        }, cancellationToken);
        return TypedResults.NoContent();
    }

    public static string Describe(LendResult result) => result switch
    {
        LendResult.NoBook => "That book is not on your shelf.",
        LendResult.NoReader => "Choose a reader on this shelf.",
        LendResult.ToSelf => "The book is already yours.",
        LendResult.AlreadyOut => "The book is on loan. Mark it returned first.",
        _ => "",
    };

    public static async Task<Results<Ok<BookResponse>, NotFound, ValidationProblem>> Lend(
        int id,
        LendRequest request,
        ShelfDb db,
        CancellationToken cancellationToken)
    {
        var result = await LendAsync(db, id, request.ReaderId, request.DueOn, cancellationToken);
        if (result == LendResult.NoBook)
        {
            return TypedResults.NotFound();
        }

        if (result != LendResult.Lent)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(LendRequest.ReaderId)] = [Describe(result)],
            });
        }

        var book = await db.Books.AsNoTracking().WithDetails().FirstAsync(item => item.Id == id, cancellationToken);
        return TypedResults.Ok(BookResponse.From(book));
    }

    public static async Task<Ok<BorrowedBook[]>> Borrowed(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await BorrowedAsync(db, cancellationToken));

    public static async Task<Results<NoContent, NotFound>> GiveBack(int id, ShelfDb db, CancellationToken cancellationToken) =>
        await GiveBackAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();
}
