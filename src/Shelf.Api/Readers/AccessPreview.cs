using Microsoft.EntityFrameworkCore;
using Shelf.Api.Books;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Readers;

// One way something reaches a reader: what it is, how much of it, what of it shows, and why.
public sealed record AccessLine(string What, int Count, string Shows, string Why, string? Owner = null);

public sealed record AccessView(string Reader, bool Admin, bool Child, AccessLine[] Lines, TokenAccess[] Tokens, AccessLine[] Public);

public sealed record TokenAccess(string Name, bool CanChange, string[] Areas, DateTimeOffset? LastUsedAt);

// Why one book reaches one reader, or that it doesn't.
public sealed record BookAccess(bool Visible, string[] Reasons);

// What one reader of this shelf can see of other readers' things, and why, worked out from the same rules the pages
// keep; for an admin to look at before sharing anything, and to answer "why can they see that?".
public static class AccessPreview
{
    public static async Task<AccessView?> ForReaderAsync(ShelfDb db, int readerId, CancellationToken cancellationToken = default)
    {
        var reader = await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (reader is null)
        {
            return null;
        }

        var books = db.Books.IgnoreQueryFilters().AsNoTracking();
        var lines = new List<AccessLine>
        {
            new(T("Their own books"), await books.CountAsync(book => book.OwnerId == readerId, cancellationToken),
                T("Everything: details, notes, reviews, quotes, highlights, files, loans."), T("They are the owner.")),
        };

        var restricted = reader.Restricted || reader.SandboxOf is not null;
        if (!restricted)
        {
            var open = await db.Readers.AsNoTracking().Where(other => other.ShelfOpen && !other.Restricted && other.Id != readerId)
                .Select(other => new { other.Id, other.Name }).ToListAsync(cancellationToken);
            foreach (var shelf in open)
            {
                lines.Add(new AccessLine(T("{0}'s open shelf", shelf.Name), await books.CountAsync(book => book.OwnerId == shelf.Id, cancellationToken),
                    T("Titles, authors, and covers, to ask to borrow. Never notes, reviews, quotes, highlights, or where the owner stopped."),
                    T("{0} opened their shelf to the other readers here.", shelf.Name), shelf.Name));
            }
        }

        var lent = await books.Where(book => book.BorrowerId == readerId).Select(book => new { book.Id, Owner = book.Owner!.Name }).ToListAsync(cancellationToken);
        foreach (var owner in lent.GroupBy(book => book.Owner))
        {
            lines.Add(new AccessLine(T("Lent to them by {0}", owner.Key), owner.Count(),
                T("The books' files to read and listen to, with their own place and highlights. Not the owner's notes or highlights."),
                T("{0} lent them, until they are given back.", owner.Key), owner.Key));
        }

        var clubs = await db.ClubMembers.IgnoreQueryFilters().AsNoTracking().Where(member => member.ReaderId == readerId)
            .Select(member => new { member.Club!.Name, Books = member.Club.Books.Count, Members = member.Club.Members.Count }).ToListAsync(cancellationToken);
        foreach (var club in clubs)
        {
            lines.Add(new AccessLine(T("The club {0}", club.Name), club.Books,
                T("The club's books, meetings, questions, and what its {0} members post there.", club.Members), T("They are a member of the club.")));
        }

        if (reader.Restricted && reader.GuardianId is int guardian)
        {
            var shared = await db.Collections.IgnoreQueryFilters().AsNoTracking().Where(list => list.OwnerId == guardian && list.ForChildren)
                .Select(list => new { list.Name, Books = list.Books.Count, Guardian = db.Readers.Where(item => item.Id == guardian).Select(item => item.Name).FirstOrDefault() })
                .ToListAsync(cancellationToken);
            foreach (var list in shared)
            {
                lines.Add(new AccessLine(T("The collection {0}", list.Name), list.Books, T("The books in it, to read."),
                    T("{0}, who looks after this account, shares it with their children.", list.Guardian ?? ""), list.Guardian));
            }
        }

        if (reader.IsAdmin)
        {
            lines.Add(new AccessLine(T("The whole server"), await books.CountAsync(cancellationToken),
                T("As an admin: every reader's name and storage, the activity log, and the snapshot download, which holds every reader's books and notes."),
                T("They are an admin.")));
        }

        var tokens = await db.ApiTokens.IgnoreQueryFilters().AsNoTracking().Where(token => token.ReaderId == readerId).OrderBy(token => token.Id)
            .Select(token => new { token.Name, token.CanChange, token.Areas, token.LastUsedAt }).ToListAsync(cancellationToken);

        // What anyone with a link sees, signed in or not.
        var publicLines = new List<AccessLine>();
        if (reader.PublicToken is not null)
        {
            publicLines.Add(new AccessLine(T("Their public page"), await books.CountAsync(book => book.OwnerId == readerId && (book.Status == BookStatus.Reading || book.Status == BookStatus.Finished), cancellationToken),
                T("What they are reading and lately finished, with their stars. Never notes, reviews, or loans."), T("They turned their public page on.")));
        }

        var sharedSearches = await db.SavedSearches.IgnoreQueryFilters().AsNoTracking().CountAsync(search => search.OwnerId == readerId && search.ShareToken != null, cancellationToken);
        var sharedLists = await db.Collections.IgnoreQueryFilters().AsNoTracking().CountAsync(list => list.OwnerId == readerId && list.ShareToken != null, cancellationToken);
        if (sharedSearches + sharedLists > 0)
        {
            publicLines.Add(new AccessLine(T("Lists shared by link"), sharedSearches + sharedLists,
                T("Titles, authors, covers, and stars, and a collection's own notes. Never notes on the books, reviews, quotes, loans, places, or files."),
                T("They shared these searches and collections by link.")));
        }

        return new AccessView(reader.Name, reader.IsAdmin, reader.Restricted, [.. lines],
            [.. tokens.Select(token => new TokenAccess(token.Name, token.CanChange, string.IsNullOrEmpty(token.Areas) ? [.. ApiTokens.Areas] : token.Areas.Split(','), token.LastUsedAt))],
            [.. publicLines]);
    }

    // Each rule that lets the reader see the book; none, and it is out of their sight.
    public static async Task<BookAccess?> ExplainAsync(ShelfDb db, int readerId, int bookId, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.IgnoreQueryFilters().AsNoTracking().Where(item => item.Id == bookId)
            .Select(item => new { item.OwnerId, item.BorrowerId, Owner = item.Owner!.Name, Open = item.Owner.ShelfOpen && !item.Owner.Restricted, item.Title })
            .FirstOrDefaultAsync(cancellationToken);
        var reader = await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == readerId, cancellationToken);
        if (book is null || reader is null)
        {
            return null;
        }

        var reasons = new List<string>();
        if (book.OwnerId == readerId)
        {
            reasons.Add(T("It is theirs."));
        }

        if (book.BorrowerId == readerId)
        {
            reasons.Add(T("{0} lent it to them.", book.Owner));
        }

        if (book.Open && book.OwnerId != readerId && !reader.Restricted && reader.SandboxOf is null)
        {
            reasons.Add(T("It is on {0}'s open shelf: its title, author, and cover only.", book.Owner));
        }

        if (reader.Restricted && reader.GuardianId is int guardian && book.OwnerId == guardian)
        {
            var lists = await db.CollectionBooks.IgnoreQueryFilters().AsNoTracking()
                .Where(entry => entry.BookId == bookId && entry.Collection!.ForChildren && entry.Collection.OwnerId == guardian).Select(entry => entry.Collection!.Name).ToListAsync(cancellationToken);
            reasons.AddRange(lists.Select(list => T("It is in {0}, a collection shared with them.", list)));
        }

        if (reader.IsAdmin && book.OwnerId != readerId)
        {
            reasons.Add(T("As an admin, they can download a snapshot of the whole server, which holds it."));
        }

        return new BookAccess(reasons.Count > 0, [.. reasons]);
    }
}
