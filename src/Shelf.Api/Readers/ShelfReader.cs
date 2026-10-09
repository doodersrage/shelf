using System.Security.Claims;

namespace Shelf.Api.Readers;

// The reader whose shelf this scope sees. A Blazor circuit sets it from its authentication state;
// a plain request falls back to the signed-in user on the current HttpContext.
public sealed class ShelfReader(IHttpContextAccessor http)
{
    private ClaimsPrincipal? _circuitUser;
    private int? _fixedId;

    public int? Id => _fixedId ?? IdOf(_circuitUser ?? http.HttpContext?.User);

    public string? Name => (_circuitUser ?? http.HttpContext?.User)?.Identity?.Name;

    public void Use(ClaimsPrincipal user) => _circuitUser = user;

    public void Use(int readerId) => _fixedId = readerId;

    public static int? IdOf(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true
        && int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;
}
