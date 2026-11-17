using System.Text.Json;
using Mira.Api.Auth;
using Mira.Core;

namespace Mira.Api.Preferences;

// GET/PUT /api/me/preferences (M3). The owner always comes from the principal; no user ID is accepted from the client.
// PUT is covered by the /api group's CSRF filter and the same-origin check. Responses and problem titles never contain
// request content, and nothing here logs preference values, the body or parser messages.
public static class PreferenceEndpoints
{
    public const string InvalidJsonTitle = "The request body must be a JSON object.";
    public const string PayloadTooLargeTitle = "The request body is too large.";
    public const string UnsupportedMediaTypeTitle = "The request body must be JSON (Content-Type: application/json).";

    public static void MapPreferenceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/me/preferences");

        group.MapGet("/", async (HttpContext http, IUserPreferenceStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.GetAsync(CurrentUser.GetUserId(http.User), cancellationToken)));

        // The body is read here rather than bound, so invalid JSON or an empty body gets a fixed 400 instead of the binder's message.
        group.MapPut("/", async (HttpContext http, IUserPreferenceStore store, CancellationToken cancellationToken) =>
        {
            if (!http.Request.HasJsonContentType())
            {
                return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: UnsupportedMediaTypeTitle);
            }

            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(http.Request.Body, default, cancellationToken);
            }
            catch (JsonException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: InvalidJsonTitle);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                // Kestrel's 256 KB body limit. Without this, Development shows the exception page with a stack trace.
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: PayloadTooLargeTitle);
            }

            using (document)
            {
                if (!PreferenceRules.TryParseUpdate(document.RootElement, out var update, out var error))
                {
                    var title = string.IsNullOrEmpty(error!.Key) ? error.Message : $"{error.Key}: {error.Message}";
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
                }

                return Results.Ok(await store.UpdateAsync(CurrentUser.GetUserId(http.User), update, cancellationToken));
            }
        });
    }
}
