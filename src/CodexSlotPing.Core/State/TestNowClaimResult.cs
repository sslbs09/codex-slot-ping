using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Eligibility;

namespace CodexSlotPing.Core.State;

public enum TestNowClaimKind
{
    Claimed,
    NotEligible,
    AlreadyConsumed,
    Blocked
}

public sealed record TestNowClaimResult(
    TestNowClaimKind Kind,
    string TestId,
    AppConfig? ConfigSnapshot,
    EligibilityDecision? EligibilityDecision)
{
    public bool IsClaimed => Kind == TestNowClaimKind.Claimed;
}
