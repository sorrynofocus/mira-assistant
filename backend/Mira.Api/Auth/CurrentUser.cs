using System.Security.Claims;

namespace Mira.Api.Auth;

public static class CurrentUser
{
    public const string SessionClaim = "mira:sid";

    // The owner of every resource comes from the authenticated principal, never from request data.
    public static Guid GetUserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty
            ? id
            : throw new InvalidOperationException("No authenticated user is available.");

    public static string? GetSessionToken(ClaimsPrincipal user) => user.FindFirstValue(SessionClaim);
}
