namespace Mira.Core;

public enum ActionClass
{
    Read,
    Write,
    Delete,
    Execute
}

// OwnerUserId must come from the authenticated principal, never from request bodies or model output.
public sealed record CapabilityRequest(Guid OwnerUserId, string Capability, ActionClass Action, string ResourceScope);

public sealed record GovernanceDecision(bool Allowed, string Reason, Guid? GrantId = null);

public interface IGovernanceService
{
    // Implementations must audit every decision, allowed or denied.
    ValueTask<GovernanceDecision> EvaluateAsync(CapabilityRequest request, CancellationToken cancellationToken = default);
}

public sealed record GovernanceAuditRecord(
    Guid Id,
    Guid OwnerUserId,
    Guid? GrantId,
    string Capability,
    ActionClass Action,
    string ResourceScope,
    bool Allowed,
    string RedactedSummary,
    DateTimeOffset CreatedAt);

public interface IGovernanceAuditLog
{
    Task RecordAsync(GovernanceAuditRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GovernanceAuditRecord>> ListForOwnerAsync(Guid ownerUserId, int limit, CancellationToken cancellationToken = default);
}
