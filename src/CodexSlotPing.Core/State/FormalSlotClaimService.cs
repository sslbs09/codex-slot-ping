using System.Text.Json;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
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
    private readonly SlotAdmissionEvaluator _admissionEvaluator;

    public FormalSlotClaimService(
        IClock clock,
        ITimeZoneProvider timeZoneProvider,
        IConfigStore configStore,
        IStateStore stateStore,
        IStateMutex stateMutex,
        SlotAdmissionEvaluator admissionEvaluator)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeZoneProvider = timeZoneProvider ?? throw new ArgumentNullException(nameof(timeZoneProvider));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _stateMutex = stateMutex ?? throw new ArgumentNullException(nameof(stateMutex));
        _admissionEvaluator = admissionEvaluator ?? throw new ArgumentNullException(nameof(admissionEvaluator));
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
            StateDocument state;
            try
            {
                config = _configStore.LoadOrCreateAsync(cancellationToken).GetAwaiter().GetResult();
                state = _stateStore.LoadAsync(cancellationToken).GetAwaiter().GetResult();
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
            var rollback = ClockRollbackGuard.Evaluate(nowUtc, state.HighestObservedUtc);
            if (rollback.IsBlocked)
            {
                return Blocked(SlotClaimBlockReason.ClockRollback);
            }

            string currentWindowsTimeZoneId;
            try
            {
                currentWindowsTimeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return SaveHighWaterOrBlock(state, rollback.NewHighestObservedUtc, SlotClaimBlockReason.TimeZoneUnavailable, cancellationToken);
            }

            var admission = _admissionEvaluator.Evaluate(config, nowUtc, currentWindowsTimeZoneId);
            if (admission.Kind == SlotAdmissionKind.Blocked)
            {
                var reason = admission.BlockReason switch
                {
                    AdmissionBlockReason.TimeZoneMismatch => SlotClaimBlockReason.TimeZoneMismatch,
                    AdmissionBlockReason.TimeZoneUnavailable => SlotClaimBlockReason.TimeZoneUnavailable,
                    _ => SlotClaimBlockReason.InvalidConfiguration
                };
                return SaveHighWaterOrBlock(state, rollback.NewHighestObservedUtc, reason, cancellationToken);
            }

            if (admission.Kind == SlotAdmissionKind.Pending)
            {
                var saveResult = SaveStateIfChanged(state, state with { HighestObservedUtc = rollback.NewHighestObservedUtc }, cancellationToken);
                return saveResult
                    ? new SlotClaimResult(SlotClaimKind.NotEligible, admission.SlotId, FormalSlotStatus.PENDING, null)
                    : Blocked(SlotClaimBlockReason.StatePersistenceFailed);
            }

            if (admission.Kind == SlotAdmissionKind.Missed)
            {
                var missedState = state with { HighestObservedUtc = rollback.NewHighestObservedUtc };
                if (admission.SlotId is not null && admission.Position is not null &&
                    !state.FormalSlots.Any(record => record.SlotId == admission.SlotId))
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
                    missedState = missedState with { FormalSlots = state.FormalSlots.Append(missedRecord).ToArray() };
                }

                return SaveStateIfChanged(state, missedState, cancellationToken)
                    ? new SlotClaimResult(SlotClaimKind.NotEligible, admission.SlotId, FormalSlotStatus.MISSED, null)
                    : Blocked(SlotClaimBlockReason.StatePersistenceFailed);
            }

            if (admission.SlotId is null || admission.Position is null)
            {
                return SaveHighWaterOrBlock(state, rollback.NewHighestObservedUtc, SlotClaimBlockReason.InvalidConfiguration, cancellationToken);
            }

            var slotId = admission.SlotId;
            var position = admission.Position.Value;
            var existingIdentity = state.FormalSlots.FirstOrDefault(record => record.SlotId == slotId);
            var claimedPositionRecord = state.FormalSlots.FirstOrDefault(record =>
                record.SlotId.LocalDate == slotId.LocalDate &&
                record.Position == position &&
                record.Status != FormalSlotStatus.MISSED);

            if (existingIdentity is not null || claimedPositionRecord is not null)
            {
                return SaveHighWaterOrBlock(
                    state,
                    rollback.NewHighestObservedUtc,
                    SlotClaimBlockReason.InvalidConfigOrState,
                    cancellationToken,
                    SlotClaimKind.AlreadyConsumed,
                    slotId,
                    existingIdentity?.Status ?? claimedPositionRecord?.Status);
            }

            if (!StateTransitions.CanTransition(null, FormalSlotStatus.CLAIMED))
            {
                return SaveHighWaterOrBlock(state, rollback.NewHighestObservedUtc, SlotClaimBlockReason.InvalidConfigOrState, cancellationToken);
            }

            var claimedRecord = new FormalSlotRecord(slotId, position, FormalSlotStatus.CLAIMED, nowUtc, null);
            var claimedState = state with
            {
                HighestObservedUtc = rollback.NewHighestObservedUtc,
                FormalSlots = state.FormalSlots.Append(claimedRecord).ToArray()
            };

            if (!TrySaveState(claimedState, cancellationToken))
            {
                return Blocked(SlotClaimBlockReason.StatePersistenceFailed);
            }

            return new SlotClaimResult(SlotClaimKind.Claimed, slotId, FormalSlotStatus.CLAIMED, null);
        }
    }

    private SlotClaimResult SaveHighWaterOrBlock(
        StateDocument originalState,
        DateTimeOffset? newHighWater,
        SlotClaimBlockReason blockReason,
        CancellationToken cancellationToken,
        SlotClaimKind resultKind = SlotClaimKind.Blocked,
        SlotId? slotId = null,
        FormalSlotStatus? status = null)
    {
        if (!SaveStateIfChanged(originalState, originalState with { HighestObservedUtc = newHighWater }, cancellationToken))
        {
            return Blocked(SlotClaimBlockReason.StatePersistenceFailed);
        }

        return new SlotClaimResult(resultKind, slotId, status, resultKind == SlotClaimKind.Blocked ? blockReason : null);
    }

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
