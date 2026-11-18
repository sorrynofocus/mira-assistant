using System.Globalization;
using System.Runtime.CompilerServices;
using Mira.Core;

namespace Mira.Infrastructure;

// Optional provider hook (M4): lets a provider label its output with the resolved persona's display name. Only the Mock uses
// it; a real provider gets the persona through ChatRequest.Instructions only.
public interface IPersonaAwareChatProvider : IChatProvider
{
    IAsyncEnumerable<string> StreamAsync(ChatRequest request, string personaName, CancellationToken cancellationToken = default);
}

public sealed class MockChatProvider(TimeSpan delay) : IPersonaAwareChatProvider
{
    // Mock-only test hook: a user message containing this marker makes the stream fail after its first delta.
    public const string ErrorTrigger = "[mock-error]";
    private const int EchoTextElements = 60;

    public MockChatProvider() : this(TimeSpan.Zero)
    {
    }

    public string ModelId => "mock-chat";

    // Deterministic echo that reflects the persona, e.g. MIRA mock (Jack) response to "...". No model was called.
    public static string ResponseFor(string userMessage, string personaName = BuiltInPersona.Name)
    {
        ArgumentNullException.ThrowIfNull(personaName);
        var info = new StringInfo(userMessage.ReplaceLineEndings(" ").Trim());
        var echo = info.LengthInTextElements > EchoTextElements
            ? info.SubstringByTextElements(0, EchoTextElements) + "…"
            : info.String;
        return $"MIRA mock ({personaName}) response to \"{echo}\". No model was called.";
    }

    public IAsyncEnumerable<string> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        StreamAsync(request, BuiltInPersona.Name, cancellationToken);

    public async IAsyncEnumerable<string> StreamAsync(
        ChatRequest request,
        string personaName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(personaName);
        var lastUser = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)
            ?? throw new ArgumentException("The request needs at least one user message.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(lastUser.Content);

        var words = ResponseFor(lastUser.Content, personaName).Split(' ');
        var fail = lastUser.Content.Contains(ErrorTrigger, StringComparison.Ordinal);
        for (var i = 0; i < words.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            else await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            yield return i < words.Length - 1 ? words[i] + " " : words[i];
            if (fail) throw new InvalidOperationException("Mock provider failure requested.");
        }
    }
}

public sealed class MockTranscriptionProvider : ITranscriptionProvider
{
    public Task<string> TranscribeAsync(Stream audio, string mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("MIRA mock transcript");
    }
}

public sealed class MockSpeechProvider : ISpeechProvider
{
    public Task<SpeechResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SpeechResult([], "audio/wav"));
    }
}
