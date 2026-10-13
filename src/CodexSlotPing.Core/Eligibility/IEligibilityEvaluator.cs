namespace CodexSlotPing.Core.Eligibility;

/// <summary>
/// Evaluates an immutable state snapshot and request without reading clocks,
/// storage, configuration, or external services.
/// </summary>
public interface IEligibilityEvaluator
{
    EligibilityDecision Evaluate(EligibilityState state, EligibilityRequest request);
}
