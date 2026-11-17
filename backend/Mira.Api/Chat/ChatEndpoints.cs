using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Mira.Api.Auth;
using Mira.Api.Conversations;
using Mira.Core;

namespace Mira.Api.Chat;

// ConversationId is optional: absent means M1's behavior (the owner's most recent conversation, or a new one).
public sealed record ChatStreamRequest(string? Message, Guid? ConversationId = null);

public static class ChatEndpoints
{
    // response.error code when the conversation is deleted while its reply streams (SSE contract stays v1).
    public const string ConversationDeletedCode = "conversation_deleted";

    public static void MapChatEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/conversations/current", async (HttpContext http, IConversationStore conversations, CancellationToken cancellationToken) =>
        {
            // Same shape as GET /api/conversations/{id}.
            var c = await conversations.GetMostRecentOrCreateAsync(CurrentUser.GetUserId(http.User), cancellationToken);
            return Results.Ok(new ConversationResponse(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.Messages));
        });

        api.MapPost("/chat/stream", StreamAsync);

        api.MapPost("/chat/cancel", (HttpContext http, ActiveTurnRegistry turns) =>
            Results.Ok(new { cancelled = turns.Cancel(CurrentUser.GetUserId(http.User)) }));
    }

    private static async Task<IResult> StreamAsync(
        ChatStreamRequest body,
        HttpContext http,
        IConversationStore conversations,
        IChatProvider provider,
        IPipelineRecorder recorder,
        ActiveTurnRegistry turns,
        MiraOptions options,
        TimeProvider time,
        IOptions<JsonOptions> jsonOptions,
        ILoggerFactory loggerFactory)
    {
        var message = body.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A message is required.");
        }

        if (message.Length > options.Chat.MaxMessageLength)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"Messages are limited to {options.Chat.MaxMessageLength} characters.");
        }

        var userId = CurrentUser.GetUserId(http.User);
        Guid conversationId;
        if (body.ConversationId is { } requested)
        {
            // Missing and someone else's conversation look the same.
            if (await conversations.GetAsync(userId, requested, http.RequestAborted) is null) return ConversationNotFound();
            conversationId = requested;
        }
        else
        {
            conversationId = (await conversations.GetMostRecentOrCreateAsync(userId, http.RequestAborted)).Id;
        }

        var turn = turns.TryBegin(userId, conversationId);
        if (turn is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A response is already streaming. Wait for it to finish or stop it.");
        }

        try
        {
            var started = time.GetTimestamp();
            var requestId = Guid.NewGuid().ToString("N");
            ConversationMessage userMessage, assistant;
            IReadOnlyList<ChatTurn> prompt;
            try
            {
                userMessage = await conversations.AppendUserMessageAsync(userId, conversationId, message, requestId, CancellationToken.None);
                prompt = await conversations.GetPromptTurnsAsync(userId, conversationId, CancellationToken.None);
                assistant = await conversations.BeginAssistantMessageAsync(userId, conversationId, provider.ModelId, requestId, CancellationToken.None);
            }
            catch (KeyNotFoundException)
            {
                // Deleted between the check and these writes. Nothing has streamed and no pipeline event was recorded, so a
                // plain 404 is still possible; the delete already removed anything that was written.
                return ConversationNotFound();
            }

            var turnContext = new TurnContext(http, userId, conversationId, requestId, started, userMessage, assistant, prompt, message.Length);
            await RunTurnAsync(turnContext, turn, conversations, provider, recorder, options, time, jsonOptions.Value.SerializerOptions,
                loggerFactory.CreateLogger(typeof(ChatEndpoints)));
        }
        finally
        {
            turns.End(userId, turn);
        }

        return Results.Empty;
    }

    private sealed record TurnContext(
        HttpContext Http, Guid UserId, Guid ConversationId, string RequestId, long Started,
        ConversationMessage UserMessage, ConversationMessage Assistant, IReadOnlyList<ChatTurn> Prompt, int InputChars);

    private static async Task RunTurnAsync(
        TurnContext ctx,
        CancellationTokenSource turn,
        IConversationStore conversations,
        IChatProvider provider,
        IPipelineRecorder recorder,
        MiraOptions options,
        TimeProvider time,
        JsonSerializerOptions json,
        ILogger logger)
    {
        var (userId, conversationId, requestId) = (ctx.UserId, ctx.ConversationId, ctx.RequestId);
        var aborted = ctx.Http.RequestAborted;
        long Elapsed() => (long)time.GetElapsedTime(ctx.Started).TotalMilliseconds;
        Task Record(string eventType, PipelineEventData data) =>
            recorder.RecordAsync(userId, requestId, conversationId, eventType, Elapsed(), data, CancellationToken.None);

        var (prompt, assistant) = (ctx.Prompt, ctx.Assistant);
        await Record(PipelineEventTypes.RequestStarted,
            new PipelineEventData(ModelId: provider.ModelId, InputChars: ctx.InputChars, PromptTurns: prompt.Count));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, turn.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(options.Chat.MaxResponseSeconds));

        var writer = new SseWriter(ctx.Http, requestId, conversationId, time, json);
        var text = new StringBuilder();
        long? firstTokenMs = null;
        var deltaCount = 0;

        // The terminal pipeline event of every outcome carries the same allowlisted fields.
        PipelineEventData Terminal(MessageStatus status, string? errorCode = null) => new(
            ModelId: provider.ModelId, Status: status, OutputChars: text.Length, DeltaCount: deltaCount,
            FirstTokenMs: firstTokenMs, TotalMs: Elapsed(), ErrorCode: errorCode);

        // False when the conversation was deleted during the turn (permanent delete, owner decision 2026-11-16). The turn then
        // records no terminal pipeline event, so nothing of this request outlives the delete (the SQLite event store also drops
        // late events for a deleted conversation), and the client is told conversation_deleted instead of the normal outcome.
        async Task<bool> FinishAsync(MessageStatus status)
        {
            try
            {
                await conversations.FinishAssistantMessageAsync(userId, conversationId, assistant.Id, text.ToString(), status, CancellationToken.None);
                return true;
            }
            catch (KeyNotFoundException)
            {
                logger.LogInformation("Chat request {RequestId}: conversation deleted during the turn.", requestId);
                return false;
            }
        }

        Task ReportDeletedAsync() => TryWriteAsync(writer, "response.error", new
        {
            messageId = assistant.Id,
            status = MessageStatus.Interrupted,
            code = ConversationDeletedCode,
            message = "This conversation was deleted, so the reply was discarded."
        }, logger);

        try
        {
            await writer.WriteAsync("response.started",
                new { messageId = assistant.Id, userMessageId = ctx.UserMessage.Id, model = provider.ModelId }, linked.Token);

            await foreach (var delta in provider.StreamAsync(new ChatRequest(prompt), linked.Token).WithCancellation(linked.Token))
            {
                if (firstTokenMs is null)
                {
                    firstTokenMs = Elapsed();
                    await Record(PipelineEventTypes.FirstToken, new PipelineEventData(FirstTokenMs: firstTokenMs));
                }

                deltaCount++;
                text.Append(delta);
                await writer.WriteAsync("response.delta", new { text = delta }, linked.Token);
            }

            if (!await FinishAsync(MessageStatus.Completed))
            {
                await ReportDeletedAsync();
                return;
            }

            await Record(PipelineEventTypes.Completed, Terminal(MessageStatus.Completed));
            await writer.WriteAsync("response.completed", new
            {
                messageId = assistant.Id,
                status = MessageStatus.Completed,
                firstTokenMs,
                totalMs = Elapsed()
            }, CancellationToken.None);
        }
        catch (Exception) when (aborted.IsCancellationRequested)
        {
            // The browser aborted the request. Keep the partial text, marked interrupted; nobody is listening for events.
            if (!await FinishAsync(MessageStatus.Interrupted)) return; // nobody is listening
            await Record(PipelineEventTypes.ClientAborted, Terminal(MessageStatus.Interrupted, PipelineErrorCodes.ClientAborted));
            logger.LogInformation("Chat request {RequestId} cancelled by the client.", requestId);
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            if (!await FinishAsync(MessageStatus.Interrupted))
            {
                await ReportDeletedAsync();
                return;
            }

            await Record(PipelineEventTypes.Cancelled, Terminal(MessageStatus.Interrupted, PipelineErrorCodes.Cancelled));
            await TryWriteAsync(writer, "response.cancelled", new { messageId = assistant.Id, status = MessageStatus.Interrupted }, logger);
        }
        catch (OperationCanceledException)
        {
            if (!await FinishAsync(MessageStatus.Interrupted))
            {
                await ReportDeletedAsync();
                return;
            }

            await Record(PipelineEventTypes.TimedOut, Terminal(MessageStatus.Interrupted, PipelineErrorCodes.Timeout));
            await TryWriteAsync(writer, "response.error", new
            {
                messageId = assistant.Id,
                status = MessageStatus.Interrupted,
                code = "timeout",
                message = "The response took too long and was stopped."
            }, logger);
        }
        catch (Exception ex)
        {
            // Log only the exception type: provider messages may echo conversation content.
            logger.LogWarning("Chat provider failed for request {RequestId}: {ErrorType}", requestId, ex.GetType().Name);
            if (!await FinishAsync(MessageStatus.Failed))
            {
                await ReportDeletedAsync();
                return;
            }

            await Record(PipelineEventTypes.Failed, Terminal(MessageStatus.Failed, PipelineErrorCodes.ProviderError));
            await TryWriteAsync(writer, "response.error", new
            {
                messageId = assistant.Id,
                status = MessageStatus.Failed,
                code = "provider_error",
                message = "The model provider failed to complete the response."
            }, logger);
        }
    }

    private static IResult ConversationNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Conversation not found.");

    private static async Task TryWriteAsync(SseWriter writer, string type, object data, ILogger logger)
    {
        try
        {
            await writer.WriteAsync(type, data, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            logger.LogDebug("Could not deliver {EventType}; the client disconnected.", type);
        }
    }
}
