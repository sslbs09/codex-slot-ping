using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Scheduler;

public sealed record SchedulingSnapshot(
    DateTimeOffset EvaluatedAtUtc,
    EligibilityDecision EligibilityDecision,
    SchedulePlan Plan);

public sealed class SchedulingCoordinator
{
    private readonly IClock _clock;
    private readonly FormalSlotClaimService _claimService;

    public SchedulingCoordinator(IClock clock, FormalSlotClaimService claimService)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _claimService = claimService ?? throw new ArgumentNullException(nameof(claimService));
    }

    public async Task<SchedulingSnapshot> EvaluateAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _clock.UtcNow.ToUniversalTime();
        var decision = await _claimService.GetEligibilityDecisionAsync(
            EligibilityRequest.Scheduled(nowUtc), cancellationToken).ConfigureAwait(false);
        return new SchedulingSnapshot(nowUtc, decision, SchedulePlan.FromDecision(decision, nowUtc));
    }

    public static SchedulePlan CreatePlan(EligibilityDecision decision, DateTimeOffset nowUtc) =>
        SchedulePlan.FromDecision(decision, nowUtc);
}
