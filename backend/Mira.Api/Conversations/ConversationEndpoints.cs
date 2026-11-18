using Mira.Api.Auth;
using Mira.Api.Chat;
using Mira.Api.Personas;
using Mira.Core;
using Mira.Infrastructure;
using Mira.Infrastructure.Sqlite;

namespace Mira.Api.Conversations;

public sealed record CreateConversationRequest(string? Title);

public sealed record RenameConversationRequest(string? Title);

// PersonaStatus: "active" when the pinned persona is built-in or still owned, "deleted" when its row is gone (soft pin; M4).
public sealed record ConversationSummaryResponse(
    Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid PersonaId, string PersonaStatus)
{
    public static ConversationSummaryResponse From(ConversationSummary c, string personaStatus) =>
        new(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.PersonaId, personaStatus);

    public static ConversationSummaryResponse From(ConversationSnapshot c, string personaStatus) =>
        new(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.PersonaId, personaStatus);
}

public static class PersonaStatuses
{
    public const string Active = "active";
    public const string Deleted = "deleted";

    public static async Task<string> ForAsync(IPersonaStore personas, Guid owner, Guid personaId, CancellationToken cancellationToken) =>
        await personas.GetAsync(owner, personaId, cancellationToken) is null ? Deleted : Active;
}

public sealed record ConversationPageResponse(IReadOnlyList<ConversationSummaryResponse> Items, int Skip, int Take, bool HasMore);

public sealed record ConversationResponse(
    Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ConversationMessage> Messages,
    Guid PersonaId, string PersonaStatus)
{
    public static async Task<ConversationResponse> FromAsync(ConversationSnapshot c, IPersonaStore personas, CancellationToken cancellationToken) =>
        new(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.Messages, c.PersonaId,
            await PersonaStatuses.ForAsync(personas, c.OwnerUserId, c.PersonaId, cancellationToken));
}

// Owner-scoped conversation CRUD (M2). The owner always comes from the principal. A missing conversation and someone else's
// look the same (404). Unsafe methods are covered by the /api group's CSRF filter and the same-origin check.
public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/conversations");

        group.MapGet("/", async (HttpContext http, IConversationStore store, IPersonaStore personas, int? skip, int? take, CancellationToken cancellationToken) =>
        {
            var s = skip ?? 0;
            var t = take ?? ConversationLimits.DefaultPageSize;
            if (s < 0 || t < 1 || t > ConversationLimits.MaxPageSize)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: $"skip must be 0 or more and take between 1 and {ConversationLimits.MaxPageSize}.");
            }

            var owner = CurrentUser.GetUserId(http.User);
            var items = await store.ListAsync(owner, s, t, cancellationToken);
            var hasMore = items.Count == t && (await store.ListAsync(owner, s + t, 1, cancellationToken)).Count > 0;
            var readable = (await personas.ListAsync(owner, cancellationToken)).Select(p => p.Id).ToHashSet();
            var page = items
                .Select(c => ConversationSummaryResponse.From(c, readable.Contains(c.PersonaId) ? PersonaStatuses.Active : PersonaStatuses.Deleted))
                .ToArray();
            return Results.Ok(new ConversationPageResponse(page, s, t, hasMore));
        });

        // A new conversation copies the owner's current default persona (a concrete pin, never "follow the default").
        group.MapPost("/", async (HttpContext http, IConversationStore store, IPersonaStore personas, CreateConversationRequest? body, CancellationToken cancellationToken) =>
        {
            if (!TryNormalize(body?.Title, out var title, out var problem)) return problem;
            var owner = CurrentUser.GetUserId(http.User);
            var created = await store.CreateAsync(owner, title, await personas.GetDefaultAsync(owner, cancellationToken), cancellationToken);
            return Results.Created($"/api/conversations/{created.Id}",
                ConversationSummaryResponse.From(created, await PersonaStatuses.ForAsync(personas, owner, created.PersonaId, cancellationToken)));
        });

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, IConversationStore store, IPersonaStore personas, CancellationToken cancellationToken) =>
            await store.GetAsync(CurrentUser.GetUserId(http.User), id, cancellationToken) is { } c
                ? Results.Ok(await ConversationResponse.FromAsync(c, personas, cancellationToken))
                : NotFound());

        // Pins the conversation to a persona for future turns. In one write transaction the conversation must be owned and the
        // persona built-in or owned; a foreign or unknown conversation or persona is a 404 (never 400).
        group.MapPut("/{id:guid}/persona", async (Guid id, HttpContext http, SqlitePersonaStore pins, CancellationToken cancellationToken) =>
        {
            var (document, problem) = await PersonaEndpoints.ReadJsonAsync(http, cancellationToken);
            if (document is null) return problem!;
            using (document)
            {
                if (!PersonaRules.TryParseDefault(document.RootElement, out var personaId, out var error)) return PersonaEndpoints.Invalid(error!);
                var owner = CurrentUser.GetUserId(http.User);
                return await pins.SetConversationPersonaAsync(owner, id, personaId, cancellationToken) switch
                {
                    ConversationPersonaResult.ConversationNotFound => NotFound(),
                    ConversationPersonaResult.PersonaNotFound => PersonaEndpoints.NotFound(),
                    _ => Results.NoContent() // pinned by MIRA Boss: the frontend expects 204
                };
            }
        });

        group.MapPatch("/{id:guid}", async (Guid id, HttpContext http, IConversationStore store, IPersonaStore personas, RenameConversationRequest body, CancellationToken cancellationToken) =>
        {
            if (!TryNormalize(body.Title, out var title, out var problem)) return problem;
            var owner = CurrentUser.GetUserId(http.User);
            if (!await store.RenameAsync(owner, id, title, cancellationToken)) return NotFound();
            return await store.GetAsync(owner, id, cancellationToken) is { } c
                ? Results.Ok(ConversationSummaryResponse.From(c, await PersonaStatuses.ForAsync(personas, owner, c.PersonaId, cancellationToken)))
                : NotFound(); // deleted concurrently
        });

        // Permanent (owner decision 2026-11-16): the conversation, its messages and its pipeline events are removed.
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, IConversationStore store, ActiveTurnRegistry turns, CancellationToken cancellationToken) =>
        {
            var owner = CurrentUser.GetUserId(http.User);
            if (!await store.DeleteAsync(owner, id, cancellationToken)) return NotFound();

            // Delete first, then cancel: the cancelled turn then finds its conversation gone, records no terminal event and
            // tells its client conversation_deleted. Waiting for it to end frees the user's streaming slot before the 204.
            await turns.CancelConversationAsync(owner, id, ActiveTurnRegistry.DeleteWait);
            return Results.NoContent();
        });
    }

    private static bool TryNormalize(string? raw, out string? title, out IResult problem)
    {
        problem = Results.Empty;
        try
        {
            title = ConversationLimits.NormalizeTitle(raw);
            return true;
        }
        catch (ArgumentException)
        {
            title = null;
            problem = Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"Titles are limited to {ConversationLimits.MaxTitleLength} characters.");
            return false;
        }
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Conversation not found.");
}
