using System.Globalization;
using System.Runtime.CompilerServices;
using Mira.Core;

namespace Mira.Infrastructure;

public sealed class MockChatProvider(TimeSpan delay) : IChatProvider
{
    // Mock-only test hook: a user message containing this marker makes the stream fail after its first delta.
    public const string ErrorTrigger = "[mock-error]";
    private const int EchoTextElements = 60;

    public MockChatProvider() : this(TimeSpan.Zero)
    {
    }

    public string ModelId => "mock-chat";

    public static string ResponseFor(string userMessage)
    {
        var info = new StringInfo(userMessage.ReplaceLineEndings(" ").Trim());
        var echo = info.LengthInTextElements > EchoTextElements
            ? info.SubstringByTextElements(0, EchoTextElements) + "…"
            : info.String;
        return $"MIRA mock response to \"{echo}\". No model was called.";
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lastUser = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)
            ?? throw new ArgumentException("The request needs at least one user message.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(lastUser.Content);

        var words = ResponseFor(lastUser.Content).Split(' ');
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
