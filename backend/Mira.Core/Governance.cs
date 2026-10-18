namespace Mira.Core;

public sealed record CapabilityRequest(string Capability, string Action, string ResourceScope);

public sealed record GovernanceDecision(bool Allowed, string Reason);

public interface IGovernanceService
{
    GovernanceDecision Evaluate(CapabilityRequest request);
}
