using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Mira.Api.Auth;
using Mira.Api.Chat;
using Mira.Api.Conversations;
using Mira.Api.Diagnostics;
using Mira.Api.Personas;
using Mira.Api.Preferences;
using Mira.Api.Security;
using Mira.Core;
using Mira.Infrastructure;
using Mira.Infrastructure.Pipeline;
using Mira.Infrastructure.Sqlite;

namespace Mira.Api;

public static class MiraHost
{
    private const long MaxRequestBodyBytes = 256 * 1024;

    // Local overrides are private; environment variables and arguments take precedence.
    public static void AddMiraConfiguration(this WebApplicationBuilder builder, string[] args)
    {
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddEnvironmentVariables(prefix: "MIRA_");
        builder.Configuration.AddCommandLine(args);
    }

    // Services shared by the web host and the create-user command.
    public static void AddMiraCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IConfiguration>()
                .GetSection(MiraOptions.SectionName).Get<MiraOptions>() ?? new MiraOptions();
            options.Validate();
            return options;
        });
        services.AddSingleton<MiraDatabase>();
        services.AddSingleton<IUserAccountService, SqliteUserAccountService>();
        services.AddSingleton<ISessionStore, SqliteSessionStore>();
        services.AddSingleton<IGovernanceAuditLog, SqliteGovernanceAuditLog>();
        services.AddSingleton<IGovernanceService, DenyAllGovernanceService>();
    }

    public static void AddMiraWeb(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddMiraCore();

        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes);
        services.Configure<KestrelServerOptions>(kestrel => kestrel.AddServerHeader = false);

        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.AddSingleton<SqlitePersonaStore>();
        services.AddSingleton<IPersonaStore>(sp => sp.GetRequiredService<SqlitePersonaStore>());
        services.AddSingleton<IUserPreferenceStore, SqliteUserPreferenceStore>();
        services.AddSingleton<IChatProvider>(sp =>
            new MockChatProvider(TimeSpan.FromMilliseconds(sp.GetRequiredService<MiraOptions>().Chat.MockDelayMilliseconds)));

        // Inspector (M2): only the configured chat provider's model ID may be stored; anything else becomes "unlisted".
        services.AddSingleton<IPipelineEventStore, SqlitePipelineEventStore>();
        services.AddSingleton<IPipelineRedactor>(sp => new PipelineRedactor([sp.GetRequiredService<IChatProvider>().ModelId]));
        services.AddSingleton<IPipelineRecorder, PipelineRecorder>();
        services.AddHostedService<PipelineRetentionService>();
        services.AddSingleton<ITranscriptionProvider, MockTranscriptionProvider>();
        services.AddSingleton<ISpeechProvider, MockSpeechProvider>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<ActiveTurnRegistry>();

        services.AddProblemDetails();
        services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = "mira.session";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                // Loopback development runs over plain HTTP; the cookie becomes Secure automatically once served over HTTPS.
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.SlidingExpiration = true;
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                cookie.Events.OnValidatePrincipal = ValidateSessionAsync;
            });
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<MiraOptions>((cookie, mira) => cookie.ExpireTimeSpan = TimeSpan.FromMinutes(mira.Auth.SessionIdleMinutes));

        // Default deny: every endpoint requires an authenticated user unless it explicitly allows anonymous access.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.HeaderName = RequestSecurity.CsrfHeaderName;
            antiforgery.Cookie.Name = "mira.csrf";
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) => new ValueTask(Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many sign-in attempts. Wait a minute and try again.").ExecuteAsync(context.HttpContext));
            limiter.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, http =>
            {
                var permits = http.RequestServices.GetRequiredService<MiraOptions>().Auth.LoginRequestsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });
    }

    public static void MapMiraEndpoints(this WebApplication app)
    {
        app.UseSameOriginWrites();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

        var api = app.MapGroup("/api").RequireCsrfForUnsafeMethods();
        api.MapAuthEndpoints();
        api.MapChatEndpoints();
        api.MapConversationEndpoints();
        api.MapDiagnosticsEndpoints();
        api.MapPersonaEndpoints();
        api.MapPreferenceEndpoints();
    }

    // API startup only (not create-user): apply migrations, then mark messages left Streaming by a crash as Interrupted.
    public static async Task InitializeMiraDatabaseAsync(this WebApplication app)
    {
        var database = app.Services.GetRequiredService<MiraDatabase>();
        await database.MigrateAsync();
        await database.InterruptStreamingMessagesAsync();
    }

    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var token = principal is null ? null : CurrentUser.GetSessionToken(principal);
        var sessions = context.HttpContext.RequestServices.GetRequiredService<ISessionStore>();
        var owner = token is null ? null : await sessions.ValidateAsync(token, context.HttpContext.RequestAborted);

        if (owner is null || principal is null || owner.Value.ToString() != principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
