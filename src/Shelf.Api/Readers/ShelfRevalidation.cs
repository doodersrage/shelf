using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Shelf.Api.Data;

namespace Shelf.Api.Readers;

// An open page keeps the reader it opened with, as it talks to the server over its own connection rather than by
// requests. Every minute it is asked again whether that sign-in still holds: a device signed out, a password changed,
// "sign out everywhere else", or an account suspended or removed ends an open page too, not only the next request.
public sealed class ShelfRevalidation(ILoggerFactory logs, IServiceScopeFactory scopes) : RevalidatingServerAuthenticationStateProvider(logs)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await StillSignedInAsync(scope.ServiceProvider.GetRequiredService<ShelfDb>(), state.User, cancellationToken);
    }

    // The same test each request passes: the password unchanged, the account not suspended, and this device not signed out.
    public static async Task<bool> StillSignedInAsync(ShelfDb db, System.Security.Claims.ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        ShelfReader.IdOf(user) is not int id
        || (await ReaderRules.StampMatchesAsync(db, id, user.FindFirst(ReaderRules.StampClaim)?.Value, cancellationToken)
            && await TwoFactor.TouchSessionAsync(db, id, user.FindFirst(TwoFactor.SessionClaim)?.Value, cancellationToken));
}
