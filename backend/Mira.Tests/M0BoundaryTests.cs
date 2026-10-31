using Mira.Core;
using Mira.Infrastructure;

namespace Mira.Tests;

public sealed class M0BoundaryTests
{
    [Fact]
    public void UnsupportedProviderModeFailsAtStartup()
    {
        var options = new MiraOptions { ProviderMode = "Azure" };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public async Task MockProvidersAreDeterministicWithoutCredentials()
    {
        IChatProvider chat = new MockChatProvider();
        var chunks = new List<string>();
        await foreach (var chunk in chat.StreamAsync(new ChatRequest([new ChatTurn(ChatRole.User, "hello")]))) chunks.Add(chunk);

        ITranscriptionProvider transcription = new MockTranscriptionProvider();
        var transcript = await transcription.TranscribeAsync(new MemoryStream([1]), "audio/wav");

        ISpeechProvider speech = new MockSpeechProvider();
        var audio = await speech.SynthesizeAsync("hello");

        Assert.True(chunks.Count > 1, "The mock should stream several deltas.");
        Assert.Equal(MockChatProvider.ResponseFor("hello"), string.Concat(chunks));
        Assert.Equal("MIRA mock transcript", transcript);
        Assert.Equal("audio/wav", audio.MediaType);
        Assert.Empty(audio.Audio);
    }

    [Fact]
    public async Task MockChatHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        IChatProvider chat = new MockChatProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in chat.StreamAsync(new ChatRequest([new ChatTurn(ChatRole.User, "hello")]), cancellation.Token)) { }
        });
    }

    [Fact]
    public void MockEchoDoesNotSplitSurrogatePairs()
    {
        var response = MockChatProvider.ResponseFor(string.Concat(Enumerable.Repeat("😀", 80)));
        // Enumerating runes maps any lone surrogate to U+FFFD.
        Assert.DoesNotContain(response.EnumerateRunes(), r => r == System.Text.Rune.ReplacementChar);
        Assert.Contains("…", response);
    }
}
