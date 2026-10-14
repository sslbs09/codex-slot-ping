using CodexSlotPing.Core.Eligibility;

namespace CodexSlotPing.Core.Testing;

public sealed record TestNowRunResponse(
    EligibilityDecision Decision,
    TestRunResult? Result);
