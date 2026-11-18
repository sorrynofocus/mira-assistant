using System.Text.RegularExpressions;
using Mira.Core;

namespace Mira.Infrastructure.Pipeline;

// Normalizes event data to the allowlist (see IPipelineRedactor). PipelineEventData has no free-text field besides ModelId and
// ErrorCode, and both are restricted here: ModelId to the configured model IDs (anything else becomes UnlistedModelId),
// ErrorCode to PipelineErrorCodes.All.
public sealed partial class PipelineRedactor : IPipelineRedactor
{
    // Stored in place of any ModelId that isn't configured, so a stray value (or a secret) never reaches the database.
    public const string UnlistedModelId = "unlisted";

    private readonly HashSet<string> _allowedModelIds;

    // allowedModelIds: the model IDs of the configured providers (in M2, the registered IChatProvider's ModelId, e.g. "mock-chat").
    // Each must be a short identifier; a malformed configured ID is a startup error, not something to store.
    public PipelineRedactor(IEnumerable<string> allowedModelIds)
    {
        ArgumentNullException.ThrowIfNull(allowedModelIds);
        _allowedModelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in allowedModelIds)
        {
            if (id is null || !ModelIdPattern().IsMatch(id) || id == UnlistedModelId)
            {
                throw new ArgumentException("Configured model IDs must be short identifiers.", nameof(allowedModelIds));
            }

            _allowedModelIds.Add(id);
        }
    }

    public PipelineEventData Redact(string eventType, PipelineEventData data)
    {
        if (eventType is null || !PipelineEventTypes.All.Contains(eventType))
        {
            throw new ArgumentException("Unknown pipeline event type.", nameof(eventType));
        }

        ArgumentNullException.ThrowIfNull(data);
        return new PipelineEventData(
            ModelId: data.ModelId is null ? null : _allowedModelIds.Contains(data.ModelId) ? data.ModelId : UnlistedModelId,
            Status: data.Status is { } status && Enum.IsDefined(status) ? status : null,
            InputChars: NonNegative(data.InputChars),
            OutputChars: NonNegative(data.OutputChars),
            PromptTurns: NonNegative(data.PromptTurns),
            DeltaCount: NonNegative(data.DeltaCount),
            FirstTokenMs: NonNegative(data.FirstTokenMs),
            TotalMs: NonNegative(data.TotalMs),
            ErrorCode: data.ErrorCode is { } code && PipelineErrorCodes.All.Contains(code) ? code : null,
            PersonaId: data.PersonaId is { } personaId && personaId != Guid.Empty ? personaId : null,
            PersonaVersion: data.PersonaVersion is > 0 ? data.PersonaVersion : null);
    }

    private static int? NonNegative(int? value) => value is >= 0 ? value : null;

    private static long? NonNegative(long? value) => value is >= 0 ? value : null;

    // \z, not $: $ would accept a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9._:/-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdPattern();
}
