using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A book club: readers on this shelf reading the same books, each book with its own conversation. The reader who
// starts a club invites the others, and can remove them or end the club; any member adds books, picks the one
// being read now, and talks about them.
public sealed class Club
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 1000;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int? CurrentBookId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<ClubMember> Members { get; set; } = [];
    public List<ClubBook> Books { get; set; } = [];
}

public sealed class ClubMember
{
    public int ClubId { get; set; }
    public Club? Club { get; set; }
    public int ReaderId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}

// A book the club reads: told by its title and author, as each member's own copy is their own.
public sealed class ClubBook
{
    public const int MaxTitleLength = 200;

    public int Id { get; set; }
    public int ClubId { get; set; }
    public Club? Club { get; set; }
    public required string Title { get; set; }
    public required string Author { get; set; }
    public string? CoverUrl { get; set; }
    public string? Isbn { get; set; }
    public int AddedById { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public List<ClubPost> Posts { get; set; } = [];
}

public sealed class ClubPost
{
    public const int MaxLength = 4000;

    public int Id { get; set; }
    public int ClubBookId { get; set; }
    public ClubBook? Book { get; set; }
    public int ReaderId { get; set; }
    public required string Text { get; set; }
    public DateTimeOffset PostedAt { get; set; }
}

public sealed record ClubSummary(int Id, string Name, string? Description, bool Mine, int Members, int Books, string? Current);

public sealed record ClubMemberResponse(int Id, string Name, bool Owner);

public sealed record ClubPostResponse(int Id, int ReaderId, string Reader, string Text, DateTimeOffset PostedAt, bool Mine);

public sealed record ClubBookResponse(int Id, string Title, string Author, string? CoverUrl, string? Isbn, string AddedBy, DateTimeOffset AddedAt, int Posts, int? OnMyShelf);

public sealed record ClubResponse(int Id, string Name, string? Description, bool Mine, int? CurrentBookId, ClubMemberResponse[] Members, ClubBookResponse[] Books);

public sealed record ClubRequest([Required, MaxLength(Club.MaxNameLength)] string Name, [MaxLength(Club.MaxDescriptionLength)] string? Description = null);

public sealed record ClubInvite(int ReaderId);

// A book from the member's own shelf (BookId), or by its title and author.
public sealed record ClubBookRequest(int? BookId = null, [MaxLength(ClubBook.MaxTitleLength)] string? Title = null, [MaxLength(ClubBook.MaxTitleLength)] string? Author = null);

public sealed record ClubPostRequest([Required, MaxLength(ClubPost.MaxLength)] string Text);

public enum ClubProblem
{
    None,
    NotFound,
    NotOwner,
    Invalid,
}

public static class Clubs
{
    public static void Map(RouteGroupBuilder books)
    {
        var clubs = books.MapGroup("/clubs");
        clubs.MapGet("/", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ListAsync(db, cancellationToken)));
        clubs.MapPost("/", async Task<IResult> (ClubRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await CreateAsync(db, request.Name, request.Description, cancellationToken) is (int id, null)
                ? TypedResults.Created($"/books/clubs/{id}", await FindAsync(db, id, cancellationToken))
                : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [T("Give the club a name.")] }));
        clubs.MapGet("/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await FindAsync(db, id, cancellationToken) is { } club ? TypedResults.Ok(club) : TypedResults.NotFound());
        clubs.MapDelete("/{id:int}", async (int id, ShelfDb db, CancellationToken cancellationToken) => Answer(await DeleteAsync(db, id, cancellationToken)));
        clubs.MapPost("/{id:int}/members", async (int id, ClubInvite invite, ShelfDb db, CancellationToken cancellationToken) => Answer(await InviteAsync(db, id, invite.ReaderId, cancellationToken)));
        clubs.MapDelete("/{id:int}/members/{readerId:int}", async (int id, int readerId, ShelfDb db, CancellationToken cancellationToken) => Answer(await RemoveMemberAsync(db, id, readerId, cancellationToken)));
        clubs.MapPost("/{id:int}/books", async (int id, ClubBookRequest request, ShelfDb db, CancellationToken cancellationToken) => Answer((await AddBookAsync(db, id, request, cancellationToken)).Problem));
        clubs.MapPut("/{id:int}/current/{bookId:int}", async (int id, int bookId, ShelfDb db, CancellationToken cancellationToken) => Answer(await SetCurrentAsync(db, id, bookId, cancellationToken)));
        clubs.MapGet("/{id:int}/books/{bookId:int}/posts", async Task<IResult> (int id, int bookId, ShelfDb db, CancellationToken cancellationToken) =>
            await PostsAsync(db, id, bookId, cancellationToken) is { } posts ? TypedResults.Ok(posts) : TypedResults.NotFound());
        clubs.MapPost("/{id:int}/books/{bookId:int}/posts", async (int id, int bookId, ClubPostRequest request, ShelfDb db, CancellationToken cancellationToken) => Answer(await PostAsync(db, id, bookId, request.Text, cancellationToken)));
        clubs.MapPost("/{id:int}/books/{bookId:int}/shelve", async Task<IResult> (int id, int bookId, ShelfDb db, CancellationToken cancellationToken) =>
            await ShelveAsync(db, id, bookId, cancellationToken) is int mine ? TypedResults.Ok(new { id = mine }) : TypedResults.NotFound());
    }

    private static IResult Answer(ClubProblem problem) => problem switch
    {
        ClubProblem.None => TypedResults.NoContent(),
        ClubProblem.NotFound => TypedResults.NotFound(),
        ClubProblem.NotOwner => TypedResults.Forbid(),
        _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Club"] = [T("That cannot be done in this club.")] }),
    };

    // Clubs are not anyone's shelf, so the books' own filter has no say here; membership does.
    private static IQueryable<Club> Mine(ShelfDb db)
    {
        var me = db.ReaderId;
        return db.Clubs.Where(club => me != 0 && club.Members.Any(member => member.ReaderId == me));
    }

    public static async Task<ClubSummary[]> ListAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var clubs = await Mine(db).AsNoTracking()
            .Select(club => new
            {
                club.Id, club.Name, club.Description, club.OwnerId,
                Members = club.Members.Count, Books = club.Books.Count,
                Current = club.Books.Where(book => book.Id == club.CurrentBookId).Select(book => book.Title).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        return clubs.OrderBy(club => club.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(club => new ClubSummary(club.Id, club.Name, club.Description, club.OwnerId == me, club.Members, club.Books, club.Current))
            .ToArray();
    }

    public static async Task<ClubResponse?> FindAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        var club = await Mine(db).AsNoTracking()
            .Include(item => item.Members)
            .Include(item => item.Books).ThenInclude(book => book.Posts)
            .AsSplitQuery()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (club is null)
        {
            return null;
        }

        var ids = club.Members.Select(member => member.ReaderId).Concat(club.Books.Select(book => book.AddedById)).Distinct().ToList();
        var names = await db.Readers.AsNoTracking().Where(reader => ids.Contains(reader.Id)).ToDictionaryAsync(reader => reader.Id, reader => reader.Name, cancellationToken);
        var shelf = await db.Books.AsNoTracking().Select(book => new { book.Id, book.Title, book.Author, book.Isbn }).ToListAsync(cancellationToken);
        return new ClubResponse(
            club.Id,
            club.Name,
            club.Description,
            club.OwnerId == me,
            club.CurrentBookId,
            club.Members.OrderBy(member => member.JoinedAt)
                .Select(member => new ClubMemberResponse(member.ReaderId, names.GetValueOrDefault(member.ReaderId, "?"), member.ReaderId == club.OwnerId))
                .ToArray(),
            club.Books.OrderByDescending(book => book.Id == club.CurrentBookId).ThenByDescending(book => book.AddedAt)
                .Select(book => new ClubBookResponse(
                    book.Id, book.Title, book.Author, book.CoverUrl, book.Isbn, names.GetValueOrDefault(book.AddedById, "?"), book.AddedAt, book.Posts.Count,
                    shelf.FirstOrDefault(own => BookRules.IsSameCopy(own.Isbn, own.Title, own.Author, book.Isbn, book.Title, book.Author))?.Id))
                .ToArray());
    }

    public static async Task<(int? Id, string? Problem)> CreateAsync(ShelfDb db, string? name, string? description, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > Club.MaxNameLength)
        {
            return (null, T("Give the club a name."));
        }

        var club = new Club { OwnerId = db.ReaderId, Name = name.Trim(), Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()[..Math.Min(description.Trim().Length, Club.MaxDescriptionLength)], CreatedAt = DateTimeOffset.UtcNow };
        club.Members.Add(new ClubMember { ReaderId = db.ReaderId, JoinedAt = DateTimeOffset.UtcNow });
        db.Clubs.Add(club);
        await db.SaveChangesAsync(cancellationToken);
        return (club.Id, null);
    }

    public static async Task<ClubProblem> DeleteAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        var club = await Mine(db).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (club is null)
        {
            return ClubProblem.NotFound;
        }

        if (club.OwnerId != db.ReaderId)
        {
            return ClubProblem.NotOwner;
        }

        db.Clubs.Remove(club);
        await db.SaveChangesAsync(cancellationToken);
        return ClubProblem.None;
    }

    public static async Task<ClubProblem> InviteAsync(ShelfDb db, int id, int readerId, CancellationToken cancellationToken = default)
    {
        var club = await Mine(db).Include(item => item.Members).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (club is null)
        {
            return ClubProblem.NotFound;
        }

        if (club.OwnerId != db.ReaderId)
        {
            return ClubProblem.NotOwner;
        }

        if (!await db.Readers.AnyAsync(reader => reader.Id == readerId, cancellationToken))
        {
            return ClubProblem.Invalid;
        }

        if (club.Members.All(member => member.ReaderId != readerId))
        {
            club.Members.Add(new ClubMember { ReaderId = readerId, JoinedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(cancellationToken);
        }

        return ClubProblem.None;
    }

    // The owner removes a member; any member removes themselves. The owner stays as long as the club does.
    public static async Task<ClubProblem> RemoveMemberAsync(ShelfDb db, int id, int readerId, CancellationToken cancellationToken = default)
    {
        var club = await Mine(db).Include(item => item.Members).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (club is null)
        {
            return ClubProblem.NotFound;
        }

        if (readerId != db.ReaderId && club.OwnerId != db.ReaderId)
        {
            return ClubProblem.NotOwner;
        }

        if (readerId == club.OwnerId || club.Members.FirstOrDefault(member => member.ReaderId == readerId) is not { } member)
        {
            return ClubProblem.Invalid;
        }

        club.Members.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        return ClubProblem.None;
    }

    public static async Task<(int? Id, ClubProblem Problem)> AddBookAsync(ShelfDb db, int id, ClubBookRequest request, CancellationToken cancellationToken = default)
    {
        if (!await Mine(db).AnyAsync(item => item.Id == id, cancellationToken))
        {
            return (null, ClubProblem.NotFound);
        }

        string title, author;
        string? cover = null, isbn = null;
        if (request.BookId is int bookId)
        {
            var own = await db.Books.AsNoTracking().FirstOrDefaultAsync(book => book.Id == bookId, cancellationToken);
            if (own is null)
            {
                return (null, ClubProblem.Invalid);
            }

            (title, author, isbn) = (own.Title, own.Author, own.Isbn);
            // A cover from elsewhere travels; one kept on the owner's shelf is theirs to show.
            cover = string.IsNullOrWhiteSpace(own.CoverUrl) ? null : own.CoverUrl;
        }
        else if (!string.IsNullOrWhiteSpace(request.Title) && !string.IsNullOrWhiteSpace(request.Author))
        {
            (title, author) = (request.Title.Trim(), request.Author.Trim());
        }
        else
        {
            return (null, ClubProblem.Invalid);
        }

        var book = new ClubBook { ClubId = id, Title = title, Author = author, CoverUrl = cover, Isbn = isbn, AddedById = db.ReaderId, AddedAt = DateTimeOffset.UtcNow };
        db.ClubBooks.Add(book);
        await db.SaveChangesAsync(cancellationToken);
        var club = await db.Clubs.FirstAsync(item => item.Id == id, cancellationToken);
        club.CurrentBookId ??= book.Id;
        await db.SaveChangesAsync(cancellationToken);
        return (book.Id, ClubProblem.None);
    }

    public static async Task<ClubProblem> SetCurrentAsync(ShelfDb db, int id, int bookId, CancellationToken cancellationToken = default)
    {
        var club = await Mine(db).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (club is null || !await db.ClubBooks.AnyAsync(book => book.Id == bookId && book.ClubId == id, cancellationToken))
        {
            return ClubProblem.NotFound;
        }

        club.CurrentBookId = bookId;
        await db.SaveChangesAsync(cancellationToken);
        return ClubProblem.None;
    }

    public static async Task<ClubPostResponse[]?> PostsAsync(ShelfDb db, int id, int bookId, CancellationToken cancellationToken = default)
    {
        if (!await Mine(db).AnyAsync(item => item.Id == id && item.Books.Any(book => book.Id == bookId), cancellationToken))
        {
            return null;
        }

        var me = db.ReaderId;
        // SQLite cannot order by a time with an offset, so the posts are put in order here.
        var posts = (await db.ClubPosts.AsNoTracking().Where(post => post.ClubBookId == bookId).ToListAsync(cancellationToken)).OrderBy(post => post.PostedAt).ThenBy(post => post.Id).ToList();
        var ids = posts.Select(post => post.ReaderId).Distinct().ToList();
        var names = await db.Readers.AsNoTracking().Where(reader => ids.Contains(reader.Id)).ToDictionaryAsync(reader => reader.Id, reader => reader.Name, cancellationToken);
        return posts.Select(post => new ClubPostResponse(post.Id, post.ReaderId, names.GetValueOrDefault(post.ReaderId, "?"), post.Text, post.PostedAt, post.ReaderId == me)).ToArray();
    }

    public static async Task<ClubProblem> PostAsync(ShelfDb db, int id, int bookId, string? text, CancellationToken cancellationToken = default)
    {
        if (!await Mine(db).AnyAsync(item => item.Id == id && item.Books.Any(book => book.Id == bookId), cancellationToken))
        {
            return ClubProblem.NotFound;
        }

        var words = text?.Trim();
        if (string.IsNullOrEmpty(words) || words.Length > ClubPost.MaxLength)
        {
            return ClubProblem.Invalid;
        }

        db.ClubPosts.Add(new ClubPost { ClubBookId = bookId, ReaderId = db.ReaderId, Text = words, PostedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return ClubProblem.None;
    }

    // A member takes a club book onto their own shelf, to read; one already there is left as it is.
    public static async Task<int?> ShelveAsync(ShelfDb db, int id, int bookId, CancellationToken cancellationToken = default)
    {
        if (!await Mine(db).AnyAsync(item => item.Id == id, cancellationToken)
            || await db.ClubBooks.AsNoTracking().FirstOrDefaultAsync(book => book.Id == bookId && book.ClubId == id, cancellationToken) is not { } wanted)
        {
            return null;
        }

        var shelf = await db.Books.AsNoTracking().Select(book => new { book.Id, book.Title, book.Author, book.Isbn }).ToListAsync(cancellationToken);
        if (shelf.FirstOrDefault(own => BookRules.IsSameCopy(own.Isbn, own.Title, own.Author, wanted.Isbn, wanted.Title, wanted.Author)) is { } already)
        {
            return already.Id;
        }

        var book = new Book { Title = wanted.Title, Author = wanted.Author, Isbn = wanted.Isbn, CoverUrl = wanted.CoverUrl, Status = BookStatus.Want, AddedAt = DateTimeOffset.UtcNow };
        db.Books.Add(book);
        await db.SaveChangesAsync(cancellationToken);
        return book.Id;
    }
}
