using Mira.Core;

namespace Mira.Infrastructure;

public sealed class DenyAllGovernanceService(IGovernanceAuditLog audit, TimeProvider time) : IGovernanceService
{
    public const string DenyReason = "No capabilities are enabled.";

    public async ValueTask<GovernanceDecision> EvaluateAsync(CapabilityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var decision = new GovernanceDecision(false, DenyReason);
        await audit.RecordAsync(new GovernanceAuditRecord(
            Guid.NewGuid(),
            request.OwnerUserId,
            null,
            request.Capability,
            request.Action,
            request.ResourceScope,
            decision.Allowed,
            $"{request.Action} on {request.Capability} denied: {DenyReason}",
            time.GetUtcNow()), cancellationToken);
        return decision;
    }
}
