using System.Text.Json;
using CodexSlotPing.Core.Codex;
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
    private readonly AutomationPauseProtection _pauseProtection;

    public FormalSlotClaimService(
        IClock clock,
        ITimeZoneProvider timeZoneProvider,
        IConfigStore configStore,
        IStateStore stateStore,
        IStateMutex stateMutex,
        SlotAdmissionEvaluator slotAdmissionEvaluator,
        IEligibilityEvaluator eligibilityEvaluator,
        AutomationPauseProtection? pauseProtection = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeZoneProvider = timeZoneProvider ?? throw new ArgumentNullException(nameof(timeZoneProvider));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _stateMutex = stateMutex ?? throw new ArgumentNullException(nameof(stateMutex));
        _slotAdmissionEvaluator = slotAdmissionEvaluator ?? throw new ArgumentNullException(nameof(slotAdmissionEvaluator));
        _eligibilityEvaluator = eligibilityEvaluator ?? throw new ArgumentNullException(nameof(eligibilityEvaluator));
        _pauseProtection = pauseProtection ?? new AutomationPauseProtection();
    }

    public bool IsPauseProtectionActive => _pauseProtection.IsActive;

    public Task<SlotClaimResult> TryClaimCurrentSlotAsync(CancellationToken cancellationToken)
    {
        // A Windows Mutex is thread-affine. Keep acquisition, the short storage section, and release on one worker thread.
        return Task.Run(() => TryClaimCurrentSlot(cancellationToken), CancellationToken.None);
    }

    public Task<SlotClaimResult> TryClaimScheduledAttemptAsync(CancellationToken cancellationToken)
    {
        // Dynamic scheduler triggers have no fixed-slot admission window. The Core evaluator and
        // the same mutex still decide and persist the single scheduled claim.
        return Task.Run(() => TryClaimScheduledAttempt(cancellationToken), CancellationToken.None);
    }

    public Task<bool> SetAutomationPausedAsync(bool isPaused, CancellationToken cancellationToken)
    {
        // Serialize control intent changes with claims so a claim either completes before
        // Pause is committed or observes the persisted pause and is rejected.
        return Task.Run(() => SetAutomationPaused(isPaused, cancellationToken), CancellationToken.None);
    }

    public Task<EligibilityDecision> GetEligibilityDecisionAsync(
        EligibilityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => GetEligibilityDecision(request, cancellationToken), CancellationToken.None);
    }

    public Task<TestNowClaimResult> TryClaimTestNowAsync(
        string testId,
        bool userConfirmed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testId);
        return Task.Run(() => TryClaimTestNow(testId, userConfirmed, cancellationToken), CancellationToken.None);
    }

    public Task<bool> FinalizeTestNowAsync(
        string testId,
        TestRunRecord record,
        CodexInvocationResult invocation,
        DateTimeOffset? attemptStartedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testId);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(invocation);
        return Task.Run(
            () => FinalizeTestNow(testId, record, invocation, attemptStartedAtUtc, cancellationToken),
            CancellationToken.None);
    }

    private EligibilityDecision GetEligibilityDecision(EligibilityRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireMutexOrThrow();
        var state = StateDocumentMigrator.MigrateV1ToV2(
            _stateStore.LoadAsync(cancellationToken).GetAwaiter().GetResult());
        return _eligibilityEvaluator.Evaluate(state.Eligibility!, request);
    }

    private bool SetAutomationPaused(bool isPaused, CancellationToken cancellationToken)
    {
        if (isPaused)
        {
            // Fail closed before persistence. The tray process and scheduled runner share this signal.
            _pauseProtection.Activate();
        }

        cancellationToken.ThrowIfCancellationRequested();
        StateMutexLease? lease;
        try
        {
            lease = _stateMutex.TryAcquire(MutexTimeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }

        if (lease is null)
        {
            return false;
        }

        using (lease)
        {
            try
            {
                var config = _configStore.LoadOrCreateAsync(cancellationToken).GetAwaiter().GetResult();
                if (config.IsPaused == isPaused)
                {
                    if (!isPaused)
                    {
                        _pauseProtection.Deactivate();
                    }

                    return true;
                }

                _configStore.SaveAsync(config with { IsPaused = isPaused }, cancellationToken)
                    .GetAwaiter().GetResult();
                if (!isPaused)
                {
                    _pauseProtection.Deactivate();
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private TestNowClaimResult TryClaimTestNow(
        string testId,
        bool userConfirmed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StateMutexLease? lease;
        try
        {
            lease = _stateMutex.TryAcquire(MutexTimeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new TestNowClaimResult(TestNowClaimKind.Blocked, testId, null, null);
        }

        if (lease is null)
        {
            return new TestNowClaimResult(TestNowClaimKind.Blocked, testId, null, null);
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
                return new TestNowClaimResult(TestNowClaimKind.Blocked, testId, null, null);
            }

            var nowUtc = _clock.UtcNow.ToUniversalTime();
            var decision = _eligibilityEvaluator.Evaluate(
                state.Eligibility! with { HighestObservedUtc = state.HighestObservedUtc },
                userConfirmed
                    ? EligibilityRequest.ConfirmedTestNow(nowUtc)
                    : EligibilityRequest.TestNow(nowUtc));

            if (string.Equals(state.ActiveTestNowClaim?.TestId, testId, StringComparison.Ordinal) ||
                string.Equals(state.LastTest?.TestId, testId, StringComparison.Ordinal))
            {
                return new TestNowClaimResult(TestNowClaimKind.AlreadyConsumed, testId, null, decision);
            }

            if (decision.Status != EligibilityStatus.Eligible)
            {
                return new TestNowClaimResult(TestNowClaimKind.NotEligible, testId, null, decision);
            }

            var highestObservedUtc = Later(state.HighestObservedUtc, nowUtc);
            var eligibility = state.Eligibility! with
            {
                HighestObservedUtc = highestObservedUtc,
                HasUnresolvedClaim = true,
                Observations = state.Eligibility.Observations
                    .Append(new EligibilityObservation(
                        EligibilityObservationKind.TestNowConfirmationGranted,
                        nowUtc,
                        ObservationSource.AppTestNow,
                        Confidence.High))
                    .ToArray()
            };
            var claimedState = state with
            {
                HighestObservedUtc = highestObservedUtc,
                Eligibility = eligibility,
                ActiveTestNowClaim = new TestNowClaimRecord(testId, nowUtc)
            };

            if (!TrySaveState(claimedState, cancellationToken))
            {
                return new TestNowClaimResult(TestNowClaimKind.Blocked, testId, null, decision);
            }

            return new TestNowClaimResult(TestNowClaimKind.Claimed, testId, config, decision);
        }
    }

    private bool FinalizeTestNow(
        string testId,
        TestRunRecord record,
        CodexInvocationResult invocation,
        DateTimeOffset? attemptStartedAtUtc,
        CancellationToken cancellationToken)
    {
        StateMutexLease? lease;
        try
        {
            lease = _stateMutex.TryAcquire(MutexTimeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }

        if (lease is null)
        {
            return false;
        }

        using (lease)
        {
            try
            {
                var state = StateDocumentMigrator.MigrateV1ToV2(
                    _stateStore.LoadAsync(cancellationToken).GetAwaiter().GetResult());
                if (!string.Equals(state.ActiveTestNowClaim?.TestId, testId, StringComparison.Ordinal) ||
                    !string.Equals(record.TestId, testId, StringComparison.Ordinal))
                {
                    return false;
                }

                var eligibility = state.Eligibility!;
                var observations = eligibility.Observations.ToList();
                var lastAttemptAtUtc = eligibility.LastAttemptAtUtc;
                var lastConfirmedSuccessAtUtc = eligibility.LastConfirmedSuccessAtUtc;
                if (invocation.ProcessStarted && attemptStartedAtUtc is { } requestStartedAtUtc)
                {
                    lastAttemptAtUtc = Later(lastAttemptAtUtc, requestStartedAtUtc);
                    observations.Add(new EligibilityObservation(
                        EligibilityObservationKind.AttemptStarted,
                        requestStartedAtUtc.ToUniversalTime(),
                        ObservationSource.AppTestNow,
                        Confidence.High));
                }

                var completedAtUtc = record.CompletedAtUtc.ToUniversalTime();
                if (invocation.ProcessStarted && invocation.Classification == CodexResultClassification.CONFIRMED_SUCCESS)
                {
                    lastConfirmedSuccessAtUtc = Later(lastConfirmedSuccessAtUtc, completedAtUtc);
                    observations.Add(new EligibilityObservation(
                        EligibilityObservationKind.ConfirmedSuccess,
                        completedAtUtc,
                        ObservationSource.AppTestNow,
                        Confidence.High));
                }

                if (invocation.ProcessStarted &&
                    string.Equals(invocation.DiagnosticCategory, "usage-limit", StringComparison.OrdinalIgnoreCase))
                {
                    observations.Add(new EligibilityObservation(
                        EligibilityObservationKind.UsageLimitReached,
                        completedAtUtc,
                        ObservationSource.AppTestNow,
                        Confidence.High));
                }

                var highestObservedUtc = Later(state.HighestObservedUtc, completedAtUtc);
                if (attemptStartedAtUtc is { } observedAttemptAtUtc)
                {
                    highestObservedUtc = Later(highestObservedUtc, observedAttemptAtUtc);
                }

                var records = state.FormalSlots;
                var finalizedEligibility = eligibility with
                {
                    HighestObservedUtc = highestObservedUtc,
                    LastAttemptAtUtc = lastAttemptAtUtc,
                    LastConfirmedSuccessAtUtc = lastConfirmedSuccessAtUtc,
                    Observations = observations
                        .OrderBy(observation => observation.ObservedAtUtc)
                        .ThenBy(observation => observation.Kind)
                        .ToArray(),
                    HasUnresolvedClaim = records.Any(item => item.Status == FormalSlotStatus.CLAIMED)
                };
                var finalizedState = state with
                {
                    HighestObservedUtc = highestObservedUtc,
                    LastTest = record with { CompletedAtUtc = completedAtUtc },
                    Eligibility = finalizedEligibility,
                    ActiveTestNowClaim = null
                };
                return TrySaveState(finalizedState, cancellationToken);
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
    }

    private StateMutexLease AcquireMutexOrThrow()
    {
        StateMutexLease? lease;
        try
        {
            lease = _stateMutex.TryAcquire(MutexTimeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new IOException("Could not acquire the state mutex.", exception);
        }

        return lease ?? throw new IOException("Could not acquire the state mutex.");
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

            if (config.IsPaused || IsPauseProtectionActive)
            {
                return Blocked(SlotClaimBlockReason.AutomationPaused);
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

    private SlotClaimResult TryClaimScheduledAttempt(CancellationToken cancellationToken)
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

            if (config.IsPaused || IsPauseProtectionActive)
            {
                return Blocked(SlotClaimBlockReason.AutomationPaused);
            }

            var nowUtc = _clock.UtcNow.ToUniversalTime();
            var stateForEvaluation = state.Eligibility! with { HighestObservedUtc = state.HighestObservedUtc };
            var decision = _eligibilityEvaluator.Evaluate(stateForEvaluation, EligibilityRequest.Scheduled(nowUtc));
            var highestObservedUtc = Later(state.HighestObservedUtc, nowUtc);
            var stateAtNow = WithHighWater(state, highestObservedUtc);

            if (decision.Status != EligibilityStatus.Eligible)
            {
                var blockedByRollback = decision.Reason == EligibilityReason.ClockRollback;
                var kind = decision.Status == EligibilityStatus.NotYetEligible
                    ? SlotClaimKind.NotEligible
                    : SlotClaimKind.Blocked;
                return SaveResultOrBlock(
                    loadedState,
                    stateAtNow,
                    kind,
                    blockedByRollback ? SlotClaimBlockReason.ClockRollback :
                        kind == SlotClaimKind.Blocked ? SlotClaimBlockReason.EligibilityNotSatisfied : null,
                    null,
                    null,
                    decision,
                    cancellationToken);
            }

            string timeZoneId;
            TimeZoneInfo timeZone;
            try
            {
                timeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
                timeZone = _timeZoneProvider.Resolve(timeZoneId);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            {
                return SaveHighWaterOrBlock(
                    loadedState, stateAtNow, SlotClaimBlockReason.TimeZoneUnavailable, cancellationToken, decision: decision);
            }

            if (!string.Equals(config.TimeZone, "SYSTEM", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(config.TimeZone, timeZoneId, StringComparison.OrdinalIgnoreCase))
            {
                return SaveHighWaterOrBlock(
                    loadedState, stateAtNow, SlotClaimBlockReason.TimeZoneMismatch, cancellationToken, decision: decision);
            }

            var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime;
            var slotId = new SlotId(timeZoneId, DateOnly.FromDateTime(localNow), TimeOnly.FromDateTime(localNow));
            var existing = stateAtNow.FormalSlots.FirstOrDefault(record => record.SlotId == slotId);
            if (existing is not null)
            {
                return SaveHighWaterOrBlock(
                    loadedState,
                    stateAtNow,
                    SlotClaimBlockReason.InvalidConfigOrState,
                    cancellationToken,
                    SlotClaimKind.AlreadyConsumed,
                    slotId,
                    existing.Status,
                    decision);
            }

            var claimedRecord = new FormalSlotRecord(slotId, 0, FormalSlotStatus.CLAIMED, nowUtc, null);
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

    private static DateTimeOffset Later(DateTimeOffset? current, DateTimeOffset candidate)
    {
        var utcCandidate = candidate.ToUniversalTime();
        return current is { } value && value.ToUniversalTime() >= utcCandidate
            ? value.ToUniversalTime()
            : utcCandidate;
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
