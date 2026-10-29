using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public static class AudioEndpoints
{
    public static async Task<IResult> Upload(
        int id,
        HttpContext http,
        ShelfDb db,
        AudioStore store,
        CoverStore covers,
        IDataProtectionProvider protection,
        Storage storage,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        if (!http.Request.HasFormContentType)
        {
            return TypedResults.Redirect($"/library/{id}?audio=unsupported");
        }

        var form = await http.Request.ReadFormAsync(cancellationToken);
        var files = form.Files.Where(file => file.Length > 0).ToList();
        if (files.Count == 0)
        {
            return TypedResults.Redirect($"/library/{id}?audio=unsupported");
        }

        var uploads = new List<AudioUpload>();
        try
        {
            foreach (var file in files)
            {
                uploads.Add(new AudioUpload(file.FileName, file.OpenReadStream(), file.Length));
            }

            var saved = await store.SaveAsync(uploads, cancellationToken);
            if (saved.Status != AudioSaveStatus.Saved || saved.StoredName is null || saved.FileName is null)
            {
                var reason = saved.Status switch
                {
                    AudioSaveStatus.TooLarge => "large",
                    AudioSaveStatus.Damaged => "damaged",
                    _ => "unsupported",
                };
                return TypedResults.Redirect($"/library/{id}?audio={reason}");
            }

            if (!await storage.FitsAsync(db, db.ReaderId, store.Size(saved.StoredName) - store.Size(book.AudioStoredName), cancellationToken))
            {
                store.Delete(saved.StoredName);
                return TypedResults.Redirect($"/library/{id}?audio=full");
            }

            // The same recording on another of this reader's books: hold it and ask before keeping a second copy.
            var keepBoth = bool.TryParse(form["keepBoth"], out var keep) && keep;
            if (!keepBoth
                && await Duplicates.FindAudioAsync(db, store, id, saved.StoredName, cancellationToken) is { } same
                && store.Hold(saved.StoredName))
            {
                var token = Duplicates.Hold(protection, new HeldUpload(db.ReaderId, id, UploadKind.Audio, saved.StoredName, saved.FileName, same.Id));
                return TypedResults.Redirect($"/library/{id}?audio=duplicate&held={Uri.EscapeDataString(token)}");
            }

            await AttachAsync(db, store, covers, book, saved.StoredName, saved.FileName, cancellationToken);
            return TypedResults.Redirect(saved.LeftOut > 0 ? $"/library/{id}?audio=saved&left={saved.LeftOut}" : $"/library/{id}?audio=saved");
        }
        finally
        {
            foreach (var upload in uploads)
            {
                await upload.Content.DisposeAsync();
            }
        }
    }

    // The old recording goes only once the book names the new one, as for e-books.
    public static async Task AttachAsync(ShelfDb db, AudioStore store, CoverStore covers, Book book, string storedName, string fileName, CancellationToken cancellationToken)
    {
        var old = book.AudioStoredName;
        book.AudioStoredName = storedName;
        book.AudioFileName = fileName;
        book.AudioTrack = 0;
        book.AudioSeconds = 0;
        book.Format ??= BookFormat.Audiobook;
        try
        {
            await Covers.NoteAudioArtAsync(book, store, covers, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            store.Delete(storedName);
            throw;
        }

        if (old != storedName)
        {
            store.Delete(old);
        }
    }

    public static async Task<Results<NoContent, NotFound>> Remove(
        int id,
        ShelfDb db,
        AudioStore store,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        var old = book.AudioStoredName;
        book.AudioStoredName = null;
        book.AudioChapterNames = null;
        book.AudioFileName = null;
        book.AudioTrack = null;
        book.AudioSeconds = null;
        await db.SaveChangesAsync(cancellationToken);
        // Its shared address goes with it, rather than waking for whatever recording comes next.
        await db.AudioFeeds.Where(feed => feed.BookId == id).ExecuteDeleteAsync(cancellationToken);
        store.Delete(old);
        return TypedResults.NoContent();
    }

    public static async Task<IResult> Track(
        int id,
        int index,
        ShelfDb db,
        AudioStore store,
        CancellationToken cancellationToken)
    {
        var book = (await Lending.OpenAsync(db, id, cancellationToken))?.Book;
        var path = book is null ? null : store.TrackPath(book.AudioStoredName, index);
        if (path is null)
        {
            return TypedResults.NotFound();
        }

        return Results.File(path, AudioStore.ContentType(path), enableRangeProcessing: true);
    }
}
