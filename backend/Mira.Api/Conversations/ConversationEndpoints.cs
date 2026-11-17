using Mira.Api.Auth;
using Mira.Api.Chat;
using Mira.Core;

namespace Mira.Api.Conversations;

public sealed record CreateConversationRequest(string? Title);

public sealed record RenameConversationRequest(string? Title);

public sealed record ConversationSummaryResponse(Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static ConversationSummaryResponse From(ConversationSummary c) => new(c.Id, c.Title, c.CreatedAt, c.UpdatedAt);

    public static ConversationSummaryResponse From(ConversationSnapshot c) => new(c.Id, c.Title, c.CreatedAt, c.UpdatedAt);
}

public sealed record ConversationPageResponse(IReadOnlyList<ConversationSummaryResponse> Items, int Skip, int Take, bool HasMore);

public sealed record ConversationResponse(
    Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ConversationMessage> Messages);

// Owner-scoped conversation CRUD (M2). The owner always comes from the principal. A missing conversation and someone else's
// look the same (404). Unsafe methods are covered by the /api group's CSRF filter and the same-origin check.
public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/conversations");

        group.MapGet("/", async (HttpContext http, IConversationStore store, int? skip, int? take, CancellationToken cancellationToken) =>
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
            return Results.Ok(new ConversationPageResponse(items.Select(ConversationSummaryResponse.From).ToArray(), s, t, hasMore));
        });

        group.MapPost("/", async (HttpContext http, IConversationStore store, CreateConversationRequest? body, CancellationToken cancellationToken) =>
        {
            if (!TryNormalize(body?.Title, out var title, out var problem)) return problem;
            var created = await store.CreateAsync(CurrentUser.GetUserId(http.User), title, cancellationToken);
            return Results.Created($"/api/conversations/{created.Id}", ConversationSummaryResponse.From(created));
        });

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, IConversationStore store, CancellationToken cancellationToken) =>
            await store.GetAsync(CurrentUser.GetUserId(http.User), id, cancellationToken) is { } c
                ? Results.Ok(new ConversationResponse(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.Messages))
                : NotFound());

        group.MapPatch("/{id:guid}", async (Guid id, HttpContext http, IConversationStore store, RenameConversationRequest body, CancellationToken cancellationToken) =>
        {
            if (!TryNormalize(body.Title, out var title, out var problem)) return problem;
            var owner = CurrentUser.GetUserId(http.User);
            if (!await store.RenameAsync(owner, id, title, cancellationToken)) return NotFound();
            return await store.GetAsync(owner, id, cancellationToken) is { } c
                ? Results.Ok(ConversationSummaryResponse.From(c))
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
