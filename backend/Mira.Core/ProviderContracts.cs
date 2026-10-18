namespace Mira.Core;

public interface IChatProvider
{
    IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken cancellationToken = default);
}

public interface ITranscriptionProvider
{
    Task<string> TranscribeAsync(Stream audio, string mediaType, CancellationToken cancellationToken = default);
}

public interface ISpeechProvider
{
    Task<SpeechResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default);
}

public sealed record SpeechResult(byte[] Audio, string MediaType);
