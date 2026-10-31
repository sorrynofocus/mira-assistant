using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Mira.Api.Auth;
using Mira.Core;

namespace Mira.Api.Chat;

public sealed record ChatStreamRequest(string? Message);

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/conversations/current", async (HttpContext http, IConversationStore conversations, CancellationToken cancellationToken) =>
        {
            var conversation = await conversations.GetOrCreateAsync(CurrentUser.GetUserId(http.User), cancellationToken);
            return Results.Ok(new { id = conversation.Id, messages = conversation.Messages });
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
        var turn = turns.TryBegin(userId);
        if (turn is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A response is already streaming. Wait for it to finish or stop it.");
        }

        try
        {
            await RunTurnAsync(http, userId, message, turn, conversations, provider, options, time, jsonOptions.Value.SerializerOptions,
                loggerFactory.CreateLogger(typeof(ChatEndpoints)));
        }
        finally
        {
            turns.End(userId, turn);
        }

        return Results.Empty;
    }

    private static async Task RunTurnAsync(
        HttpContext http,
        Guid userId,
        string message,
        CancellationTokenSource turn,
        IConversationStore conversations,
        IChatProvider provider,
        MiraOptions options,
        TimeProvider time,
        JsonSerializerOptions json,
        ILogger logger)
    {
        var started = time.GetTimestamp();
        var requestId = Guid.NewGuid().ToString("N");
        var aborted = http.RequestAborted;

        var conversation = await conversations.GetOrCreateAsync(userId, CancellationToken.None);
        var userMessage = await conversations.AppendUserMessageAsync(userId, message, CancellationToken.None);
        var prompt = await conversations.GetPromptTurnsAsync(userId, CancellationToken.None);
        var assistant = await conversations.BeginAssistantMessageAsync(userId, provider.ModelId, CancellationToken.None);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, turn.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(options.Chat.MaxResponseSeconds));

        var writer = new SseWriter(http, requestId, conversation.Id, time, json);
        var text = new StringBuilder();
        long? firstTokenMs = null;

        try
        {
            await writer.WriteAsync("response.started",
                new { messageId = assistant.Id, userMessageId = userMessage.Id, model = provider.ModelId }, linked.Token);

            await foreach (var delta in provider.StreamAsync(new ChatRequest(prompt), linked.Token).WithCancellation(linked.Token))
            {
                firstTokenMs ??= (long)time.GetElapsedTime(started).TotalMilliseconds;
                text.Append(delta);
                await writer.WriteAsync("response.delta", new { text = delta }, linked.Token);
            }

            await conversations.FinishAssistantMessageAsync(userId, assistant.Id, text.ToString(), MessageStatus.Completed, CancellationToken.None);
            await writer.WriteAsync("response.completed", new
            {
                messageId = assistant.Id,
                status = MessageStatus.Completed,
                firstTokenMs,
                totalMs = (long)time.GetElapsedTime(started).TotalMilliseconds
            }, CancellationToken.None);
        }
        catch (Exception) when (aborted.IsCancellationRequested)
        {
            // The browser aborted the request. Keep the partial text, marked interrupted; nobody is listening for events.
            await conversations.FinishAssistantMessageAsync(userId, assistant.Id, text.ToString(), MessageStatus.Interrupted, CancellationToken.None);
            logger.LogInformation("Chat request {RequestId} cancelled by the client.", requestId);
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            await conversations.FinishAssistantMessageAsync(userId, assistant.Id, text.ToString(), MessageStatus.Interrupted, CancellationToken.None);
            await TryWriteAsync(writer, "response.cancelled", new { messageId = assistant.Id, status = MessageStatus.Interrupted }, logger);
        }
        catch (OperationCanceledException)
        {
            await conversations.FinishAssistantMessageAsync(userId, assistant.Id, text.ToString(), MessageStatus.Interrupted, CancellationToken.None);
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
            await conversations.FinishAssistantMessageAsync(userId, assistant.Id, text.ToString(), MessageStatus.Failed, CancellationToken.None);
            await TryWriteAsync(writer, "response.error", new
            {
                messageId = assistant.Id,
                status = MessageStatus.Failed,
                code = "provider_error",
                message = "The model provider failed to complete the response."
            }, logger);
        }
    }

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
