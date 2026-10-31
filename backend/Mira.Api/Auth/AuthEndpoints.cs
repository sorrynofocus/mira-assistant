using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Mira.Core;

namespace Mira.Api.Auth;

public sealed record LoginRequest(string? Username, string? Password);

public sealed record UserDto(Guid Id, string Username);

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
                Results.Ok(new { csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken }))
            .AllowAnonymous();

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimitPolicy);

        auth.MapPost("/logout", LogoutAsync);

        auth.MapGet("/me", async (HttpContext http, IUserAccountService accounts, CancellationToken cancellationToken) =>
        {
            var account = await accounts.FindByIdAsync(CurrentUser.GetUserId(http.User), cancellationToken);
            return account is null ? Results.Unauthorized() : Results.Ok(new UserDto(account.Id, account.Username));
        });
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest body,
        HttpContext http,
        IUserAccountService accounts,
        ISessionStore sessions,
        LoginThrottle throttle,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(AuthEndpoints));
        var throttleKey = UsernameRules.NormalizeKey(body.Username);
        if (throttle.IsLockedOut(throttleKey))
        {
            logger.LogWarning("Login throttled after repeated failures.");
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many failed attempts. Try again later.");
        }

        // Never log the request body: it contains the password.
        var account = await accounts.VerifyCredentialsAsync(body.Username ?? string.Empty, body.Password ?? string.Empty, cancellationToken);
        if (account is null)
        {
            throttle.RecordFailure(throttleKey);
            logger.LogInformation("Login rejected.");
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid username or password.");
        }

        throttle.Reset(throttleKey);

        // Regenerate the session on every login: retire any session the caller already carried.
        if (CurrentUser.GetSessionToken(http.User) is { } previous) await sessions.RevokeAsync(previous, cancellationToken);

        var token = await sessions.CreateAsync(account.Id, cancellationToken);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
                new Claim(ClaimTypes.Name, account.Username),
                new Claim(CurrentUser.SessionClaim, token)
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        logger.LogInformation("Login succeeded for user {UserId}.", account.Id);

        // Antiforgery tokens are bound to the user, so issue a fresh one for the new identity.
        http.User = principal;
        return Results.Ok(new
        {
            user = new UserDto(account.Id, account.Username),
            csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken
        });
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http,
        ISessionStore sessions,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (CurrentUser.GetSessionToken(http.User) is { } token) await sessions.RevokeAsync(token, cancellationToken);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        http.User = new ClaimsPrincipal(new ClaimsIdentity());
        return Results.Ok(new { csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken });
    }
}
