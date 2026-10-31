using Microsoft.AspNetCore.Antiforgery;
using Mira.Core;

namespace Mira.Api.Security;

public static class RequestSecurity
{
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    public static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    // Defense in depth alongside antiforgery tokens and SameSite cookies: refuse state-changing requests from other origins.
    public static IApplicationBuilder UseSameOriginWrites(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (!IsSafeMethod(http.Request.Method))
        {
            var origin = http.Request.Headers.Origin.ToString();
            var allowed = http.RequestServices.GetRequiredService<MiraOptions>().ResolveAllowedOrigins();
            var crossSite = string.Equals(http.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase);
            if (crossSite || (origin.Length > 0 && !allowed.Contains(origin, StringComparer.OrdinalIgnoreCase)))
            {
                await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Cross-origin request refused.").ExecuteAsync(http);
                return;
            }
        }

        await next(http);
    });

    // Every unsafe method in the group must carry a valid antiforgery request token in the X-CSRF-TOKEN header.
    public static RouteGroupBuilder RequireCsrfForUnsafeMethods(this RouteGroupBuilder group) => group.AddEndpointFilter(async (context, next) =>
    {
        var http = context.HttpContext;
        if (!IsSafeMethod(http.Request.Method))
        {
            var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
            if (!await antiforgery.IsRequestValidAsync(http))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "CSRF validation failed.");
            }
        }

        return await next(context);
    });
}
