using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// Send to Kindle: an e-book emailed to the reader's Kindle address, as Amazon's own service takes them. Amazon
// accepts EPUB and PDF by email, up to 50 MB, and only from addresses on the reader's approved list. A borrowed book
// is not sent: the copy on the Kindle would outlast the loan.
public static class Kindle
{
    public const long MaxBytes = 50L * 1024 * 1024;

    public static bool Takes(Book book) => EbookStore.IsEpub(book.EbookStoredName) || EbookStore.IsPdf(book.EbookStoredName);

    // Null once it is sent, otherwise why not.
    public static async Task<string?> SendAsync(ShelfDb db, EbookStore store, IEmailSender email, int bookId, CancellationToken cancellationToken)
    {
        if (!email.Enabled)
        {
            return T("This shelf has no mail server set up, so it cannot send to a Kindle.");
        }

        var reader = await db.Readers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == db.ReaderId, cancellationToken);
        if (reader?.KindleEmail is not { Length: > 0 } address)
        {
            return T("Add your Kindle's email address on Account first.");
        }

        if (await Lending.OpenAsync(db, bookId, cancellationToken) is not { } open || open.Borrowed)
        {
            return T("Only a book of your own can be sent to a Kindle.");
        }

        var book = open.Book;
        if (!Takes(book) || store.OpenPath(book.EbookStoredName) is not { } path)
        {
            return T("A Kindle takes an EPUB or a PDF by email; this book has neither.");
        }

        if (new FileInfo(path).Length > MaxBytes)
        {
            return T("Amazon takes files of up to 50 MB by email, and this one is larger.");
        }

        var pdf = EbookStore.IsPdf(book.EbookStoredName);
        var name = NotesExport.FileName(book).Replace(".md", pdf ? ".pdf" : ".epub", StringComparison.Ordinal);
        try
        {
            await email.SendAsync(new EmailMessage(
                address,
                book.Title,
                T("{0}, sent from Shelf.", book.Title),
                [new EmailAttachment(name, pdf ? "application/pdf" : "application/epub+zip", path)]), cancellationToken);
        }
        catch (Exception ex) when (ex is MailKit.ServiceNotConnectedException or MailKit.Net.Smtp.SmtpCommandException or MailKit.Net.Smtp.SmtpProtocolException
            or MailKit.Security.AuthenticationException or System.Net.Sockets.SocketException or IOException)
        {
            return T("The mail server did not take it: {0}", ex.Message);
        }

        await Audit.NoteAsync(db, Say("Sent a book to a Kindle"), detail: book.Title, cancellationToken: cancellationToken);
        return null;
    }

    public static async Task<IResult> Send(int id, ShelfDb db, EbookStore store, IEmailSender email, CancellationToken cancellationToken) =>
        await SendAsync(db, store, email, id, cancellationToken) is { } problem
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Kindle"] = [problem] })
            : TypedResults.NoContent();
}
