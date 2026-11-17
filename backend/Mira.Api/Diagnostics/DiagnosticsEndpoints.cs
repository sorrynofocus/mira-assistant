using System.Text.RegularExpressions;
using Mira.Api.Auth;
using Mira.Core;

namespace Mira.Api.Diagnostics;

public sealed record PipelineEventResponse(string EventType, long ElapsedMs, DateTimeOffset CreatedAt, PipelineEventData Data);

public sealed record RequestTraceResponse(string RequestId, Guid? ConversationId, IReadOnlyList<PipelineEventResponse> Events);

public sealed record RequestListResponse(IReadOnlyList<PipelineRequestSummary> Items);

// Read-only, owner-scoped inspector data (M2). Payloads are the redacted allowlist only; event and owner IDs are not exposed.
public static partial class DiagnosticsEndpoints
{
    public const int DefaultRequestCount = 20;

    public static void MapDiagnosticsEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/diagnostics");

        group.MapGet("/", async (HttpContext http, IPipelineEventStore events, IConversationStore conversations,
            Guid? conversationId, int? take, CancellationToken cancellationToken) =>
        {
            var t = take ?? DefaultRequestCount;
            if (t < 1 || t > PipelineEventRetention.MaxRequestsPerQuery)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: $"take must be between 1 and {PipelineEventRetention.MaxRequestsPerQuery}.");
            }

            var owner = CurrentUser.GetUserId(http.User);
            if (conversationId is { } id && await conversations.GetAsync(owner, id, cancellationToken) is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Conversation not found.");
            }

            return Results.Ok(new RequestListResponse(await events.ListRecentRequestsAsync(owner, conversationId, t, cancellationToken)));
        });

        group.MapGet("/{requestId}", async (string requestId, HttpContext http, IPipelineEventStore events, CancellationToken cancellationToken) =>
        {
            var found = RequestIdPattern().IsMatch(requestId)
                ? await events.GetByRequestAsync(CurrentUser.GetUserId(http.User), requestId, cancellationToken)
                : [];
            if (found.Count == 0) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Request not found.");

            return Results.Ok(new RequestTraceResponse(
                requestId,
                found[0].ConversationId,
                found.Select(e => new PipelineEventResponse(e.EventType, e.ElapsedMs, e.CreatedAt, e.Data)).ToArray()));
        });
    }

    // Request IDs are Guid "N" format: 32 lowercase hex characters.
    [GeneratedRegex("^[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();
}
