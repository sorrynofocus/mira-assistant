using Mira.Core;

namespace Mira.Infrastructure;

public sealed class DenyAllGovernanceService : IGovernanceService
{
    public GovernanceDecision Evaluate(CapabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new GovernanceDecision(false, "No capabilities are enabled in M0.");
    }
}
