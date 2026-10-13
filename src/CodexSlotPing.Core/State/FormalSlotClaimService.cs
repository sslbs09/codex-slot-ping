using System.Text.Json;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.State;

public sealed class FormalSlotClaimService
{
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(2);
    private readonly IClock _clock;
    private readonly ITimeZoneProvider _timeZoneProvider;
    private readonly IConfigStore _configStore;
    private readonly IStateStore _stateStore;
    private readonly IStateMutex _stateMutex;
    private readonly SlotAdmissionEvaluator _slotAdmissionEvaluator;
    private readonly IEligibilityEvaluator _eligibilityEvaluator;

    public FormalSlotClaimService(
        IClock clock,
        ITimeZoneProvider timeZoneProvider,
        IConfigStore configStore,
        IStateStore stateStore,
        IStateMutex stateMutex,
        SlotAdmissionEvaluator slotAdmissionEvaluator,
        IEligibilityEvaluator eligibilityEvaluator)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeZoneProvider = timeZoneProvider ?? throw new ArgumentNullException(nameof(timeZoneProvider));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _stateMutex = stateMutex ?? throw new ArgumentNullException(nameof(stateMutex));
        _slotAdmissionEvaluator = slotAdmissionEvaluator ?? throw new ArgumentNullException(nameof(slotAdmissionEvaluator));
        _eligibilityEvaluator = eligibilityEvaluator ?? throw new ArgumentNullException(nameof(eligibilityEvaluator));
    }

    public Task<SlotClaimResult> TryClaimCurrentSlotAsync(CancellationToken cancellationToken)
    {
        // A Windows Mutex is thread-affine. Keep acquisition, the short storage section, and release on one worker thread.
        return Task.Run(() => TryClaimCurrentSlot(cancellationToken), CancellationToken.None);
    }

    private SlotClaimResult TryClaimCurrentSlot(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StateMutexLease? lease;
        try
        {
            lease = _stateMutex.TryAcquire(MutexTimeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Blocked(SlotClaimBlockReason.MutexUnavailable);
        }

        if (lease is null)
        {
            return Blocked(SlotClaimBlockReason.MutexUnavailable);
        }

        using (lease)
        {
            AppConfig config;
            StateDocument loadedState;
            StateDocument state;
            try
            {
                config = _configStore.LoadOrCreateAsync(cancellationToken).GetAwaiter().GetResult();
                loadedState = _stateStore.LoadAsync(cancellationToken).GetAwaiter().GetResult();
                state = StateDocumentMigrator.MigrateV1ToV2(loadedState);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
            {
                return Blocked(SlotClaimBlockReason.InvalidConfigOrState);
            }

            if (!config.Enabled)
            {
                return Blocked(SlotClaimBlockReason.ConfigurationDisabled);
            }

            var nowUtc = _clock.UtcNow.ToUniversalTime();
            var eligibilityState = state.Eligibility! with { HighestObservedUtc = state.HighestObservedUtc };
            var decision = _eligibilityEvaluator.Evaluate(
                eligibilityState,
                EligibilityRequest.Scheduled(nowUtc));
            if (decision.Reason == EligibilityReason.ClockRollback)
            {
                return SaveResultOrBlock(
                    loadedState,
                    state,
                    SlotClaimKind.Blocked,
                    SlotClaimBlockReason.ClockRollback,
                    null,
                    null,
                    decision,
                    cancellationToken);
            }

            var newHighestObservedUtc = state.HighestObservedUtc is { } priorHighWater && priorHighWater > nowUtc
                ? priorHighWater
                : nowUtc;
            var stateAtNow = WithHighWater(state, newHighestObservedUtc);

            if (decision.Status != EligibilityStatus.Eligible)
            {
                var resultKind = decision.Status == EligibilityStatus.NotYetEligible
                    ? SlotClaimKind.NotEligible
                    : SlotClaimKind.Blocked;
                SlotClaimBlockReason? blockReason = resultKind == SlotClaimKind.Blocked
                    ? SlotClaimBlockReason.EligibilityNotSatisfied
                    : null;
                return SaveResultOrBlock(
                    loadedState,
                    stateAtNow,
                    resultKind,
                    blockReason,
                    null,
                    resultKind == SlotClaimKind.NotEligible ? FormalSlotStatus.PENDING : null,
                    decision,
                    cancellationToken);
            }

            string currentWindowsTimeZoneId;
            try
            {
                currentWindowsTimeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return SaveHighWaterOrBlock(loadedState, stateAtNow, SlotClaimBlockReason.TimeZoneUnavailable, cancellationToken, decision: decision);
            }

            var admission = _slotAdmissionEvaluator.Evaluate(config, nowUtc, currentWindowsTimeZoneId);
            if (admission.Kind == SlotAdmissionKind.Blocked)
            {
                var reason = admission.BlockReason switch
                {
                    AdmissionBlockReason.TimeZoneMismatch => SlotClaimBlockReason.TimeZoneMismatch,
                    AdmissionBlockReason.TimeZoneUnavailable => SlotClaimBlockReason.TimeZoneUnavailable,
                    _ => SlotClaimBlockReason.InvalidConfiguration
                };
                return SaveHighWaterOrBlock(loadedState, stateAtNow, reason, cancellationToken, decision: decision);
            }

            if (admission.Kind == SlotAdmissionKind.Pending)
            {
                return SaveResultOrBlock(
                    loadedState,
                    stateAtNow,
                    SlotClaimKind.NotEligible,
                    null,
                    admission.SlotId,
                    FormalSlotStatus.PENDING,
                    decision,
                    cancellationToken);
            }

            if (admission.Kind == SlotAdmissionKind.Missed)
            {
                var missedState = stateAtNow;
                if (admission.SlotId is not null && admission.Position is not null &&
                    !stateAtNow.FormalSlots.Any(record => record.SlotId == admission.SlotId))
                {
                    if (!StateTransitions.CanTransition(null, FormalSlotStatus.MISSED))
                    {
                        return Blocked(SlotClaimBlockReason.InvalidConfigOrState);
                    }

                    var missedRecord = new FormalSlotRecord(
                        admission.SlotId,
                        admission.Position.Value,
                        FormalSlotStatus.MISSED,
                        nowUtc,
                        "admission-window-expired");
                    missedState = missedState with { FormalSlots = stateAtNow.FormalSlots.Append(missedRecord).ToArray() };
                }

                return SaveResultOrBlock(
                    loadedState,
                    missedState,
                    SlotClaimKind.NotEligible,
                    null,
                    admission.SlotId,
                    FormalSlotStatus.MISSED,
                    decision,
                    cancellationToken);
            }

            if (admission.SlotId is null || admission.Position is null)
            {
                return SaveHighWaterOrBlock(loadedState, stateAtNow, SlotClaimBlockReason.InvalidConfiguration, cancellationToken, decision: decision);
            }

            var slotId = admission.SlotId;
            var position = admission.Position.Value;
            var existingIdentity = stateAtNow.FormalSlots.FirstOrDefault(record => record.SlotId == slotId);
            var claimedPositionRecord = stateAtNow.FormalSlots.FirstOrDefault(record =>
                record.SlotId.LocalDate == slotId.LocalDate &&
                record.Position == position &&
                record.Status != FormalSlotStatus.MISSED);

            if (existingIdentity is not null || claimedPositionRecord is not null)
            {
                return SaveHighWaterOrBlock(
                    loadedState,
                    stateAtNow,
                    SlotClaimBlockReason.InvalidConfigOrState,
                    cancellationToken,
                    SlotClaimKind.AlreadyConsumed,
                    slotId,
                    existingIdentity?.Status ?? claimedPositionRecord?.Status,
                    decision);
            }

            if (!StateTransitions.CanTransition(null, FormalSlotStatus.CLAIMED))
            {
                return SaveHighWaterOrBlock(loadedState, stateAtNow, SlotClaimBlockReason.InvalidConfigOrState, cancellationToken, decision: decision);
            }

            var claimedRecord = new FormalSlotRecord(slotId, position, FormalSlotStatus.CLAIMED, nowUtc, null);
            var claimedState = stateAtNow with
            {
                FormalSlots = stateAtNow.FormalSlots.Append(claimedRecord).ToArray(),
                Eligibility = stateAtNow.Eligibility! with { HasUnresolvedClaim = true }
            };

            if (!TrySaveState(claimedState, cancellationToken))
            {
                return Blocked(SlotClaimBlockReason.StatePersistenceFailed);
            }

            return new SlotClaimResult(SlotClaimKind.Claimed, slotId, FormalSlotStatus.CLAIMED, null)
            {
                ConfigSnapshot = config,
                EligibilityDecision = decision
            };
        }
    }

    private SlotClaimResult SaveHighWaterOrBlock(
        StateDocument originalState,
        StateDocument updatedState,
        SlotClaimBlockReason blockReason,
        CancellationToken cancellationToken,
        SlotClaimKind resultKind = SlotClaimKind.Blocked,
        SlotId? slotId = null,
        FormalSlotStatus? status = null,
        EligibilityDecision? decision = null)
    {
        return SaveResultOrBlock(
            originalState,
            updatedState,
            resultKind,
            resultKind == SlotClaimKind.Blocked ? blockReason : null,
            slotId,
            status,
            decision,
            cancellationToken);
    }

    private SlotClaimResult SaveResultOrBlock(
        StateDocument originalState,
        StateDocument updatedState,
        SlotClaimKind resultKind,
        SlotClaimBlockReason? blockReason,
        SlotId? slotId,
        FormalSlotStatus? status,
        EligibilityDecision? decision,
        CancellationToken cancellationToken)
    {
        if (!SaveStateIfChanged(originalState, updatedState, cancellationToken))
        {
            return Blocked(SlotClaimBlockReason.StatePersistenceFailed);
        }

        return new SlotClaimResult(resultKind, slotId, status, blockReason)
        {
            EligibilityDecision = decision
        };
    }

    private static StateDocument WithHighWater(StateDocument state, DateTimeOffset? highestObservedUtc) =>
        state with
        {
            HighestObservedUtc = highestObservedUtc,
            Eligibility = state.Eligibility! with { HighestObservedUtc = highestObservedUtc }
        };

    private bool SaveStateIfChanged(StateDocument originalState, StateDocument updatedState, CancellationToken cancellationToken)
    {
        if (Equals(originalState, updatedState))
        {
            return true;
        }

        return TrySaveState(updatedState, cancellationToken);
    }

    private bool TrySaveState(StateDocument state, CancellationToken cancellationToken)
    {
        try
        {
            _stateStore.SaveAsync(state, cancellationToken).GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SlotClaimResult Blocked(SlotClaimBlockReason reason) =>
        new(SlotClaimKind.Blocked, null, null, reason);
}
