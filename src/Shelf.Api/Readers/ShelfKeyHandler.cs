using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Shelf.Api.Data;

namespace Shelf.Api.Readers;

// Another shelf signs its sync requests with "Authorization: Bearer <key>", using a key made on this shelf.
// An e-reader's OPDS client sends the same key as the password of HTTP Basic sign-in; the user name is not used.
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
        string? key = null;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            key = header["Bearer ".Length..];
        }
        else if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var colon = pair.IndexOf(':');
                key = colon >= 0 ? pair[(colon + 1)..] : null;
            }
            catch (FormatException)
            {
                return AuthenticateResult.Fail("That sign-in could not be read.");
            }
        }

        if (key is null)
        {
            return AuthenticateResult.NoResult();
        }

        var reader = await ReaderRules.FindByKeyAsync(db, key, Context.RequestAborted);
        return reader is null
            ? AuthenticateResult.Fail("That key does not open a shelf here.")
            : AuthenticateResult.Success(new AuthenticationTicket(ReaderRules.Principal(reader, SchemeName), SchemeName));
    }

    // E-readers only ask for a user name and password when the answer says how to sign in.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        if (Request.Path.StartsWithSegments("/opds"))
        {
            Response.Headers.WWWAuthenticate = "Basic realm=\"Shelf\", charset=\"UTF-8\"";
        }

        return Task.CompletedTask;
    }
}
