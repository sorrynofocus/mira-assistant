using System.Runtime.CompilerServices;
using Mira.Core;

namespace Mira.Infrastructure;

public sealed class MockChatProvider : IChatProvider
{
    public async IAsyncEnumerable<string> StreamAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return "MIRA mock response";
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
