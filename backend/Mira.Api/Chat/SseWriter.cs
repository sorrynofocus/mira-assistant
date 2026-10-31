using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace Mira.Api.Chat;

// Writes SSE-formatted events. JSON serialization escapes newlines, so each event is a single data line.
public sealed class SseWriter
{
    public const int SchemaVersion = 1;

    private readonly HttpResponse _response;
    private readonly string _requestId;
    private readonly Guid _conversationId;
    private readonly TimeProvider _time;
    private readonly JsonSerializerOptions _json;

    public SseWriter(HttpContext http, string requestId, Guid conversationId, TimeProvider time, JsonSerializerOptions json)
    {
        _response = http.Response;
        _requestId = requestId;
        _conversationId = conversationId;
        _time = time;
        _json = json;

        _response.ContentType = "text/event-stream; charset=utf-8";
        _response.Headers.CacheControl = "no-cache, no-transform";
        _response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    public async Task WriteAsync(string type, object data, CancellationToken cancellationToken)
    {
        var envelope = new SseEnvelope(SchemaVersion, _requestId, _conversationId, type, _time.GetUtcNow(), data);
        var payload = JsonSerializer.Serialize(envelope, _json);
        await _response.WriteAsync($"event: {type}\ndata: {payload}\n\n", cancellationToken);
        await _response.Body.FlushAsync(cancellationToken);
    }

    private sealed record SseEnvelope(int V, string RequestId, Guid ConversationId, string Type, DateTimeOffset Timestamp, object Data);
}
