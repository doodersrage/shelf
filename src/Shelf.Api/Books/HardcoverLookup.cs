using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// Hardcover as a catalog, after Open Library and Google Books: it knows newer books well, and their series and
// numbers, which the others seldom give. Asked with the reader's own Hardcover token (Account), or the shelf's
// (Hardcover:LookupToken); with neither, not asked at all.
public interface IHardcoverLookup
{
    Task<CatalogMatch?> FindAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken);
}

public sealed class HardcoverLookup(IHardcoverClient client, ShelfDb db, IDataProtectionProvider protection, IConfiguration configuration) : IHardcoverLookup
{
    private string? token;
    private bool looked;

    private async Task<string?> TokenAsync(CancellationToken cancellationToken)
    {
        if (looked)
        {
            return token;
        }

        looked = true;
        if (db.ReaderId != 0
            && await db.Readers.AsNoTracking().Where(reader => reader.Id == db.ReaderId).Select(reader => reader.HardcoverToken).FirstOrDefaultAsync(cancellationToken) is { Length: > 0 } sealedToken)
        {
            try
            {
                token = protection.CreateProtector("Shelf.Hardcover.Token").Unprotect(sealedToken);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                token = null;
            }
        }

        token ??= configuration["Hardcover:LookupToken"] is { Length: > 0 } shared ? shared : null;
        return token;
    }

    public async Task<CatalogMatch?> FindAsync(string? isbn, string? title, string? author, CancellationToken cancellationToken)
    {
        if (await TokenAsync(cancellationToken) is not { } key || (string.IsNullOrWhiteSpace(isbn) && string.IsNullOrWhiteSpace(title)))
        {
            return null;
        }

        var id = await client.FindAsync(key, BookRules.NormalizeIsbn(isbn), title ?? "", author ?? "", cancellationToken);
        return id is int found ? await client.DetailsAsync(key, found, cancellationToken) : null;
    }
}
