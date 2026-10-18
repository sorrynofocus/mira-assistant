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
    public void GovernanceDeniesEveryCapability()
    {
        IGovernanceService governance = new DenyAllGovernanceService();
        var decision = governance.Evaluate(new CapabilityRequest("web", "write", "example.com"));
        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task MockProvidersAreDeterministicWithoutCredentials()
    {
        IChatProvider chat = new MockChatProvider();
        var chunks = new List<string>();
        await foreach (var chunk in chat.StreamAsync("hello")) chunks.Add(chunk);

        ITranscriptionProvider transcription = new MockTranscriptionProvider();
        var transcript = await transcription.TranscribeAsync(new MemoryStream([1]), "audio/wav");

        ISpeechProvider speech = new MockSpeechProvider();
        var audio = await speech.SynthesizeAsync("hello");

        Assert.Equal(["MIRA mock response"], chunks);
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
            await foreach (var _ in chat.StreamAsync("hello", cancellation.Token)) { }
        });
    }
}
