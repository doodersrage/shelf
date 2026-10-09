using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Shelf.Api.Data;

namespace Shelf.Api.Readers;

// Another shelf signs its sync requests with "Authorization: Bearer <key>", using a key made on this shelf.
public sealed class ShelfKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ShelfDb db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ShelfKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var reader = await ReaderRules.FindByKeyAsync(db, header["Bearer ".Length..], Context.RequestAborted);
        return reader is null
            ? AuthenticateResult.Fail("That key does not open a shelf here.")
            : AuthenticateResult.Success(new AuthenticationTicket(ReaderRules.Principal(reader, SchemeName), SchemeName));
    }
}
