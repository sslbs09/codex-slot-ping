using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Tests.State;

public sealed class FormalSlotClaimServiceTests
{
    private const string EasternTimeZoneId = "Eastern Standard Time";
    private static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById(EasternTimeZoneId);

    [Fact]
    public async Task EligibleClaim_IsDurablySavedWhileMutexIsHeldBeforeReturning()
    {
        var nowUtc = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1));
        var fixture = CreateFixture(nowUtc);

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Claimed, result.Kind);
        Assert.Equal(FormalSlotStatus.CLAIMED, result.Status);
        Assert.Equal(new SlotId(EasternTimeZoneId, new DateOnly(2026, 1, 15), new TimeOnly(7, 0)), result.SlotId);
        Assert.Single(fixture.StateStore.State.FormalSlots);
        Assert.Equal(FormalSlotStatus.CLAIMED, fixture.StateStore.State.FormalSlots[0].Status);
        Assert.True(fixture.StateStore.SaveObservedMutexHeld);
        Assert.False(fixture.Mutex.IsHeld);
        Assert.Equal(1, fixture.Clock.ReadCount);
        Assert.Equal(EligibilityStatus.Eligible, result.EligibilityDecision!.Status);
    }

    [Fact]
    public async Task ScheduledAttemptClaim_DoesNotRequireALegacyFixedSlotWindow()
    {
        var nowUtc = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(13, 37, 12));
        var fixture = CreateFixture(nowUtc);

        var result = await fixture.Service.TryClaimScheduledAttemptAsync(CancellationToken.None);

        Assert.True(result.IsClaimed);
        Assert.Equal(0, fixture.StateStore.State.FormalSlots[0].Position);
        Assert.Equal(new TimeOnly(13, 37, 12), result.SlotId!.LocalTime);
        Assert.Equal(EligibilityStatus.Eligible, result.EligibilityDecision!.Status);
        Assert.True(fixture.StateStore.SaveObservedMutexHeld);
    }

    [Fact]
    public async Task PausedAutomation_BlocksScheduledClaimsButAllowsConfirmedTestNow()
    {
        var now = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(13, 37, 12));
        var fixture = CreateFixture(now, config: AppConfigDefaults.Create() with
        {
            TimeZone = EasternTimeZoneId,
            Enabled = true,
            IsPaused = true
        });
        var originalState = fixture.StateStore.State;

        var scheduledAttempt = await fixture.Service.TryClaimScheduledAttemptAsync(CancellationToken.None);
        var legacyScheduledAttempt = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);
        Assert.Equal(originalState, fixture.StateStore.State);
        var unconfirmedTestNow = await fixture.Service.TryClaimTestNowAsync(
            "paused-test-now-unconfirmed",
            userConfirmed: false,
            CancellationToken.None);
        var testNow = await fixture.Service.TryClaimTestNowAsync(
            "paused-test-now",
            userConfirmed: true,
            CancellationToken.None);

        Assert.Equal(SlotClaimKind.Blocked, scheduledAttempt.Kind);
        Assert.Equal(SlotClaimBlockReason.AutomationPaused, scheduledAttempt.BlockReason);
        Assert.Equal(SlotClaimKind.Blocked, legacyScheduledAttempt.Kind);
        Assert.Equal(SlotClaimBlockReason.AutomationPaused, legacyScheduledAttempt.BlockReason);
        Assert.Equal(TestNowClaimKind.NotEligible, unconfirmedTestNow.Kind);
        Assert.Equal(EligibilityReason.TestNowConfirmationRequired, unconfirmedTestNow.EligibilityDecision!.Reason);
        Assert.True(testNow.IsClaimed);
        Assert.Equal("paused-test-now", fixture.StateStore.State.ActiveTestNowClaim!.TestId);
    }

    [Fact]
    public async Task SetAutomationPausedAsync_PersistsIntentUnderClaimMutex()
    {
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(13, 37, 12)));

        var saved = await fixture.Service.SetAutomationPausedAsync(true, CancellationToken.None);

        Assert.True(saved);
        Assert.True(fixture.ConfigStore.Config.Enabled);
        Assert.True(fixture.ConfigStore.Config.IsPaused);
        Assert.True(fixture.ConfigStore.SaveObservedMutexHeld);
        Assert.False(fixture.Mutex.IsHeld);
    }

    [Fact]
    public async Task PauseConfigWriteFailure_ProtectsConcurrentScheduledClaimsButAllowsTestNow()
    {
        var now = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1));
        var fixture = CreateFixture(now);
        fixture.ConfigStore.SaveFailure = new IOException("injected config write failure");
        fixture.ConfigStore.BlockSaveFailure = true;

        var pauseTask = fixture.Service.SetAutomationPausedAsync(true, CancellationToken.None);
        Assert.True(await Task.Run(() => fixture.ConfigStore.SaveStarted.Wait(TimeSpan.FromSeconds(2))));

        var scheduledTask = fixture.Service.TryClaimScheduledAttemptAsync(CancellationToken.None);
        Assert.True(await Task.Run(() => fixture.Mutex.SecondAcquireAttempted.Wait(TimeSpan.FromSeconds(2))));
        fixture.ConfigStore.AllowSaveFailure.Set();

        var pausePersisted = await pauseTask;
        var scheduled = await scheduledTask;
        var legacyScheduled = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.False(pausePersisted);
        Assert.Equal(SlotClaimKind.Blocked, scheduled.Kind);
        Assert.Equal(SlotClaimBlockReason.AutomationPaused, scheduled.BlockReason);
        Assert.Equal(SlotClaimKind.Blocked, legacyScheduled.Kind);
        Assert.Equal(SlotClaimBlockReason.AutomationPaused, legacyScheduled.BlockReason);

        var testNowFixture = CreateFixture(now);
        testNowFixture.ConfigStore.SaveFailure = new IOException("injected config write failure");
        Assert.False(await testNowFixture.Service.SetAutomationPausedAsync(true, CancellationToken.None));
        var testNow = await testNowFixture.Service.TryClaimTestNowAsync(
            "pause-write-failure-test-now", userConfirmed: true, CancellationToken.None);
        Assert.True(testNow.IsClaimed);
    }

    [Fact]
    public async Task PauseConfigWriteFailure_ProtectsClaimsInSeparateRunnerService()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var now = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1));
        var eventName = $@"Local\CodexSlotPing.Tests.{Guid.NewGuid():N}";
        using var appProtection = new AutomationPauseProtection(eventName);
        using var runnerProtection = new AutomationPauseProtection(eventName);
        var fixture = CreateFixture(now, pauseProtection: appProtection);
        var runner = new FormalSlotClaimService(
            fixture.Clock,
            new FixedTimeZoneProvider(EasternTimeZoneId, EasternTimeZone),
            fixture.ConfigStore,
            fixture.StateStore,
            fixture.Mutex,
            new SlotAdmissionEvaluator(new FixedTimeZoneProvider(EasternTimeZoneId, EasternTimeZone)),
            new EligibilityEvaluator(),
            runnerProtection);
        fixture.ConfigStore.SaveFailure = new IOException("injected config write failure");

        Assert.False(await fixture.Service.SetAutomationPausedAsync(true, CancellationToken.None));

        var scheduled = await runner.TryClaimScheduledAttemptAsync(CancellationToken.None);
        var testNow = await runner.TryClaimTestNowAsync(
            "separate-runner-test-now", userConfirmed: true, CancellationToken.None);

        Assert.Equal(SlotClaimBlockReason.AutomationPaused, scheduled.BlockReason);
        Assert.True(testNow.IsClaimed);
    }

    [Fact]
    public async Task ConcurrentScheduledAttemptClaims_HaveOneWinner()
    {
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(13, 37, 12)));

        var results = await Task.WhenAll(
            fixture.Service.TryClaimScheduledAttemptAsync(CancellationToken.None),
            fixture.Service.TryClaimScheduledAttemptAsync(CancellationToken.None));

        Assert.Single(results, result => result.IsClaimed);
        Assert.Single(results, result => result.EligibilityDecision?.Reason == EligibilityReason.UnresolvedClaim);
        Assert.Single(fixture.StateStore.State.FormalSlots);
        Assert.Equal(0, Assert.Single(fixture.StateStore.State.FormalSlots).Position);
    }

    [Fact]
    public async Task SecondClaimForTheSameIdentity_IsRejected()
    {
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)));

        var first = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);
        var second = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Claimed, first.Kind);
        Assert.Equal(SlotClaimKind.Blocked, second.Kind);
        Assert.Equal(EligibilityReason.UnresolvedClaim, second.EligibilityDecision!.Reason);
        Assert.Null(second.Status);
        Assert.Single(fixture.StateStore.State.FormalSlots);
        Assert.Equal(1, fixture.StateStore.SaveCount);
    }

    [Fact]
    public async Task ClaimedPosition_CannotBeClaimedAgainAfterItsTimeIsEdited()
    {
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)));
        var first = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);
        fixture.ConfigStore.Config = AppConfigDefaults.Create() with
        {
            TimeZone = EasternTimeZoneId,
            LegacyFixedSlots = ["08:00", "12:01", "17:01", "22:01"]
        };
        fixture.Clock.Now = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(8, 1));

        var second = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Claimed, first.Kind);
        Assert.Equal(SlotClaimKind.Blocked, second.Kind);
        Assert.Equal(EligibilityReason.UnresolvedClaim, second.EligibilityDecision!.Reason);
        Assert.Single(fixture.StateStore.State.FormalSlots);
        Assert.Equal(new TimeOnly(7, 0), fixture.StateStore.State.FormalSlots[0].SlotId.LocalTime);
    }

    [Fact]
    public async Task EditedUnclaimedFutureIdentity_CanBeClaimedAfterOldIdentityWasMissed()
    {
        var date = new DateOnly(2026, 1, 15);
        var state = KnownState() with
        {
            FormalSlots = [Record(date, "07:00", 1, FormalSlotStatus.CONFIRMED_SUCCESS), Record(date, "12:01", 2, FormalSlotStatus.MISSED)]
        };
        var config = AppConfigDefaults.Create() with
        {
            TimeZone = EasternTimeZoneId,
            LegacyFixedSlots = ["07:00", "12:10", "17:01", "22:01"]
        };
        var fixture = CreateFixture(AtLocal(date, new TimeOnly(12, 10)), config, state);

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Claimed, result.Kind);
        Assert.Equal(new SlotId(EasternTimeZoneId, date, new TimeOnly(12, 10)), result.SlotId);
        Assert.Equal(3, fixture.StateStore.State.FormalSlots.Count);
    }

    [Fact]
    public async Task ExpiredSlot_IsPersistedAsMissedAndNeverClaimed()
    {
        var date = new DateOnly(2026, 1, 15);
        var fixture = CreateFixture(AtLocal(date, new TimeOnly(7, 4)));

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.NotEligible, result.Kind);
        Assert.Equal(FormalSlotStatus.MISSED, result.Status);
        var missed = Assert.Single(fixture.StateStore.State.FormalSlots);
        Assert.Equal(new SlotId(EasternTimeZoneId, date, new TimeOnly(7, 0)), missed.SlotId);
        Assert.Equal(FormalSlotStatus.MISSED, missed.Status);
    }

    [Fact]
    public async Task UnavailableMutex_BlocksBeforeLoadingStateOrReadingClock()
    {
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)), mutexAvailable: false);

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Blocked, result.Kind);
        Assert.Equal(SlotClaimBlockReason.MutexUnavailable, result.BlockReason);
        Assert.Equal(0, fixture.ConfigStore.LoadCount);
        Assert.Equal(0, fixture.StateStore.LoadCount);
        Assert.Equal(0, fixture.Clock.ReadCount);
    }

    [Fact]
    public async Task InvalidState_BlocksClaim()
    {
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)));
        fixture.StateStore.LoadFailure = new InvalidDataException("invalid state fixture");

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Blocked, result.Kind);
        Assert.Equal(SlotClaimBlockReason.InvalidConfigOrState, result.BlockReason);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
    }

    [Fact]
    public async Task TimezoneMismatch_BlocksClaim()
    {
        var config = AppConfigDefaults.Create() with { TimeZone = "Pacific Standard Time" };
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)), config);

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Blocked, result.Kind);
        Assert.Equal(SlotClaimBlockReason.TimeZoneMismatch, result.BlockReason);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
    }

    [Fact]
    public async Task BackwardClockMove_BlocksAndPreservesPersistedHighWaterMark()
    {
        var highWater = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var knownState = KnownState();
        var state = knownState with
        {
            HighestObservedUtc = highWater,
            Eligibility = knownState.Eligibility! with { HighestObservedUtc = highWater }
        };
        var fixture = CreateFixture(highWater.AddMinutes(-1), state: state);

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Blocked, result.Kind);
        Assert.Equal(SlotClaimBlockReason.ClockRollback, result.BlockReason);
        Assert.Equal(highWater, fixture.StateStore.State.HighestObservedUtc);
        Assert.Equal(0, fixture.StateStore.SaveCount);
    }

    [Fact]
    public async Task ClaimPersistenceFailure_BlocksWithoutReturningAClaim()
    {
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)));
        fixture.StateStore.SaveFailure = new IOException("injected atomic write failure");

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Blocked, result.Kind);
        Assert.Equal(SlotClaimBlockReason.StatePersistenceFailed, result.BlockReason);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
    }

    [Fact]
    public async Task DisabledConfig_BlocksWithoutReadingClockOrWritingState()
    {
        var config = AppConfigDefaults.Create() with
        {
            Enabled = false,
            IsPaused = true,
            TimeZone = EasternTimeZoneId
        };
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)), config);

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Blocked, result.Kind);
        Assert.Equal(SlotClaimBlockReason.ConfigurationDisabled, result.BlockReason);
        Assert.Equal(0, fixture.Clock.ReadCount);
        Assert.Equal(0, fixture.StateStore.SaveCount);
    }

    [Fact]
    public async Task UnknownExternalUsage_AllowsLocalScheduledClaimAndReturnsCoreExplanation()
    {
        var fixture = CreateFixture(
            AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)),
            state: StateDocument.Empty());

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Claimed, result.Kind);
        Assert.Equal(EligibilityStatus.Eligible, result.EligibilityDecision!.Status);
        Assert.Equal(EligibilityReason.Ready, result.EligibilityDecision.Reason);
        Assert.Equal(EligibilityReason.ExternalManualUsageUnknown, result.EligibilityDecision.ExplanationReason);
        Assert.Single(fixture.StateStore.State.FormalSlots);
    }

    [Fact]
    public async Task UsageLimitWithoutTrustedReset_BlocksScheduledClaim()
    {
        var now = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1));
        var knownState = KnownState();
        var state = knownState with
        {
            Eligibility = knownState.Eligibility! with
            {
                Observations = [new EligibilityObservation(
                    EligibilityObservationKind.UsageLimitReached,
                    now.AddMinutes(-1),
                    ObservationSource.AppScheduled,
                    Confidence.High)]
            }
        };
        var fixture = CreateFixture(now, state: state);

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Blocked, result.Kind);
        Assert.Equal(EligibilityReason.UsageLimitWithoutTrustedReset, result.EligibilityDecision!.Reason);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
    }

    [Fact]
    public async Task TestNowUsageLimitWithoutConfirmation_DoesNotClaim()
    {
        var now = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1));
        var knownState = KnownState();
        var state = knownState with
        {
            Eligibility = knownState.Eligibility! with
            {
                Observations = [new EligibilityObservation(
                    EligibilityObservationKind.UsageLimitReached,
                    now.AddMinutes(-1),
                    ObservationSource.AppScheduled,
                    Confidence.High)]
            }
        };
        var fixture = CreateFixture(now, state: state);

        var result = await fixture.Service.TryClaimTestNowAsync("test-request-001", userConfirmed: false,
            CancellationToken.None);

        Assert.Equal(TestNowClaimKind.NotEligible, result.Kind);
        Assert.Equal(EligibilityStatus.ConfirmationRequired, result.EligibilityDecision!.Status);
        Assert.Equal(EligibilityReason.TestNowConfirmationRequired, result.EligibilityDecision.Reason);
        Assert.Null(fixture.StateStore.State.ActiveTestNowClaim);
        Assert.Equal(0, fixture.StateStore.SaveCount);
    }

    [Fact]
    public async Task ConfirmedTestNowClaimsOnceAndUsageLimitStillBlocksScheduledClaim()
    {
        var now = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1));
        var knownState = KnownState();
        var usageLimit = new EligibilityObservation(
            EligibilityObservationKind.UsageLimitReached,
            now.AddMinutes(-1),
            ObservationSource.AppScheduled,
            Confidence.High);
        var state = knownState with
        {
            Eligibility = knownState.Eligibility! with { Observations = [usageLimit] }
        };
        var fixture = CreateFixture(now, state: state);

        var claim = await fixture.Service.TryClaimTestNowAsync("test-request-002", userConfirmed: true,
            CancellationToken.None);
        var duplicate = await fixture.Service.TryClaimTestNowAsync("test-request-002", userConfirmed: true,
            CancellationToken.None);

        Assert.True(claim.IsClaimed);
        Assert.Equal(EligibilityReason.TestNowExplicitlyConfirmed, claim.EligibilityDecision!.Reason);
        Assert.Equal("test-request-002", fixture.StateStore.State.ActiveTestNowClaim!.TestId);
        Assert.True(fixture.StateStore.State.Eligibility!.HasUnresolvedClaim);
        Assert.Contains(usageLimit, fixture.StateStore.State.Eligibility.Observations);
        Assert.Contains(fixture.StateStore.State.Eligibility.Observations,
            observation => observation.Kind == EligibilityObservationKind.TestNowConfirmationGranted &&
                           observation.Source == ObservationSource.AppTestNow);
        Assert.Equal(TestNowClaimKind.AlreadyConsumed, duplicate.Kind);
        Assert.Equal(1, fixture.StateStore.SaveCount);

        var whileActive = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);
        Assert.Equal(EligibilityReason.UnresolvedClaim, whileActive.EligibilityDecision!.Reason);

        var invocation = new CodexInvocationResult(
            CodexResultClassification.ATTEMPTED_UNCONFIRMED,
            true,
            1,
            50,
            false,
            null,
            Array.Empty<string>(),
            null,
            "usage-limit");
        var record = new TestRunRecord("test-request-002", now.AddSeconds(30), "gpt-5.6-luna", "low",
            "usage-limit", 50, 1);
        Assert.True(await fixture.Service.FinalizeTestNowAsync("test-request-002", record, invocation, now,
            CancellationToken.None));

        Assert.Null(fixture.StateStore.State.ActiveTestNowClaim);
        Assert.False(fixture.StateStore.State.Eligibility!.HasUnresolvedClaim);
        Assert.Contains(usageLimit, fixture.StateStore.State.Eligibility.Observations);
        Assert.Contains(fixture.StateStore.State.Eligibility.Observations,
            observation => observation.Kind == EligibilityObservationKind.UsageLimitReached &&
                           observation.Source == ObservationSource.AppTestNow &&
                           observation.TrustedResetAtUtc is null);
        fixture.Clock.Now = now.AddMinutes(1);
        var scheduled = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);
        Assert.Equal(EligibilityReason.UsageLimitWithoutTrustedReset, scheduled.EligibilityDecision!.Reason);
    }

    [Fact]
    public async Task ConfirmedTestNowWithoutUsageLimit_CanClaimOneManualAttempt()
    {
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)));

        var results = await Task.WhenAll(
            fixture.Service.TryClaimTestNowAsync("test-request-003", userConfirmed: true, CancellationToken.None),
            fixture.Service.TryClaimTestNowAsync("test-request-003", userConfirmed: true, CancellationToken.None));

        Assert.Single(results, result => result.IsClaimed);
        Assert.Single(results, result => result.Kind == TestNowClaimKind.AlreadyConsumed);
        Assert.Equal(EligibilityReason.TestNowExplicitlyConfirmed,
            Assert.Single(results, result => result.IsClaimed).EligibilityDecision!.Reason);
        Assert.Equal(1, fixture.StateStore.SaveCount);
        Assert.Equal("test-request-003", fixture.StateStore.State.ActiveTestNowClaim!.TestId);
    }

    [Fact]
    public async Task ConfirmedTestNowCanClaimWhileExternalUsageIsUnknown_AndSchedulerUsesLocalPolicy()
    {
        var now = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1));
        var fixture = CreateFixture(now, state: StateDocument.Empty());

        var preview = await fixture.Service.GetEligibilityDecisionAsync(
            EligibilityRequest.TestNow(now), CancellationToken.None);
        var unconfirmed = await fixture.Service.TryClaimTestNowAsync(
            "test-request-unknown-usage", userConfirmed: false, CancellationToken.None);
        var confirmed = await fixture.Service.TryClaimTestNowAsync(
            "test-request-unknown-usage", userConfirmed: true, CancellationToken.None);

        Assert.Equal(EligibilityStatus.ConfirmationRequired, preview.Status);
        Assert.True(preview.RequiresConfirmation);
        Assert.Equal(TestNowClaimKind.NotEligible, unconfirmed.Kind);
        Assert.True(confirmed.IsClaimed);
        Assert.Equal(EligibilityReason.TestNowExplicitlyConfirmed, confirmed.EligibilityDecision!.Reason);
        Assert.True(fixture.StateStore.State.Eligibility!.ExternalManualUsageUnknown);

        var invocation = new CodexInvocationResult(
            CodexResultClassification.PRELAUNCH_FAILED,
            false,
            null,
            0,
            false,
            null,
            Array.Empty<string>(),
            null,
            "cli-environment-not-ready");
        var record = new TestRunRecord("test-request-unknown-usage", now, "gpt-5.6-luna", "low",
            "cli-environment-not-ready", 0, null);
        Assert.True(await fixture.Service.FinalizeTestNowAsync(
            "test-request-unknown-usage", record, invocation, null, CancellationToken.None));

        fixture.Clock.Now = now.AddMinutes(1);
        var scheduled = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);
        Assert.True(scheduled.IsClaimed);
        Assert.Equal(EligibilityStatus.Eligible, scheduled.EligibilityDecision!.Status);
        Assert.Equal(EligibilityReason.Ready, scheduled.EligibilityDecision.Reason);
        Assert.Equal(EligibilityReason.ExternalManualUsageUnknown, scheduled.EligibilityDecision.ExplanationReason);
        Assert.True(fixture.StateStore.State.Eligibility!.ExternalManualUsageUnknown);
    }

    [Fact]
    public async Task ConcurrentClaimsForSameSlot_HaveOneWinner()
    {
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)));

        var results = await Task.WhenAll(
            fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None),
            fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None));

        Assert.Single(results, result => result.Kind == SlotClaimKind.Claimed);
        Assert.Single(fixture.StateStore.State.FormalSlots);
        Assert.Equal(1, fixture.StateStore.SaveCount);
    }

    private static ClaimFixture CreateFixture(
        DateTimeOffset nowUtc,
        AppConfig? config = null,
        StateDocument? state = null,
        bool mutexAvailable = true,
        AutomationPauseProtection? pauseProtection = null)
    {
        var clock = new FixedClock(nowUtc);
        var zoneProvider = new FixedTimeZoneProvider(EasternTimeZoneId, EasternTimeZone);
        var mutex = new RecordingStateMutex(mutexAvailable);
        var configStore = new InMemoryConfigStore(
            config ?? AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId },
            () => mutex.IsHeld);
        var stateStore = new InMemoryStateStore(state ?? KnownState(), () => mutex.IsHeld);
        var service = new FormalSlotClaimService(
            clock,
            zoneProvider,
            configStore,
            stateStore,
            mutex,
            new SlotAdmissionEvaluator(zoneProvider),
            new EligibilityEvaluator(),
            pauseProtection);
        return new ClaimFixture(service, clock, configStore, stateStore, mutex);
    }

    private static StateDocument KnownState() => StateDocument.Empty() with
    {
        Eligibility = EligibilityState.Empty() with { ExternalManualUsageUnknown = false }
    };

    private static FormalSlotRecord Record(DateOnly date, string time, int position, FormalSlotStatus status) =>
        new(new SlotId(EasternTimeZoneId, date, TimeOnly.Parse(time)), position, status, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null);

    private static DateTimeOffset AtLocal(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, EasternTimeZone), TimeSpan.Zero);
    }

    private sealed record ClaimFixture(
        FormalSlotClaimService Service,
        FixedClock Clock,
        InMemoryConfigStore ConfigStore,
        InMemoryStateStore StateStore,
        RecordingStateMutex Mutex);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; set; } = now;

        public int ReadCount { get; private set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                ReadCount++;
                return Now;
            }
        }
    }

    private sealed class FixedTimeZoneProvider(string timeZoneId, TimeZoneInfo timeZone) : ITimeZoneProvider
    {
        public string CurrentWindowsTimeZoneId => timeZoneId;

        public TimeZoneInfo Resolve(string windowsTimeZoneId) =>
            string.Equals(windowsTimeZoneId, timeZoneId, StringComparison.OrdinalIgnoreCase)
                ? timeZone
                : throw new TimeZoneNotFoundException(windowsTimeZoneId);
    }

    private sealed class InMemoryConfigStore(AppConfig config, Func<bool> isMutexHeld) : IConfigStore
    {
        public AppConfig Config { get; set; } = config;

        public Exception? SaveFailure { get; set; }

        public bool BlockSaveFailure { get; set; }

        public ManualResetEventSlim SaveStarted { get; } = new();

        public ManualResetEventSlim AllowSaveFailure { get; } = new();

        public bool SaveObservedMutexHeld { get; private set; }

        public int LoadCount { get; private set; }

        public Task<AppConfig> LoadOrCreateAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(Config);
        }

        public Task SaveAsync(AppConfig config, CancellationToken cancellationToken)
        {
            SaveObservedMutexHeld |= isMutexHeld();
            if (SaveFailure is not null)
            {
                SaveStarted.Set();
                if (BlockSaveFailure && !AllowSaveFailure.Wait(TimeSpan.FromSeconds(5)))
                {
                    return Task.FromException(new TimeoutException("Config write failure barrier timed out."));
                }

                return Task.FromException(SaveFailure);
            }

            Config = config;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryStateStore(StateDocument state, Func<bool> isMutexHeld) : IStateStore
    {
        public StateDocument State { get; private set; } = state;

        public int LoadCount { get; private set; }

        public int SaveCount { get; private set; }

        public bool SaveObservedMutexHeld { get; private set; }

        public Exception? LoadFailure { get; set; }

        public Exception? SaveFailure { get; set; }

        public Task<StateDocument> LoadAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return LoadFailure is null
                ? Task.FromResult(State)
                : Task.FromException<StateDocument>(LoadFailure);
        }

        public Task SaveAsync(StateDocument state, CancellationToken cancellationToken)
        {
            SaveCount++;
            SaveObservedMutexHeld |= isMutexHeld();
            if (SaveFailure is not null)
            {
                return Task.FromException(SaveFailure);
            }

            State = state;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStateMutex(bool available) : IStateMutex
    {
        private readonly object _gate = new();
        private int _acquireAttemptCount;

        public bool IsHeld { get; private set; }

        public ManualResetEventSlim SecondAcquireAttempted { get; } = new();

        public StateMutexLease? TryAcquire(TimeSpan timeout)
        {
            if (Interlocked.Increment(ref _acquireAttemptCount) >= 2)
            {
                SecondAcquireAttempted.Set();
            }

            if (!available || !Monitor.TryEnter(_gate, timeout))
            {
                return null;
            }

            IsHeld = true;
            return new StateMutexLease(() =>
            {
                IsHeld = false;
                Monitor.Exit(_gate);
            });
        }
    }
}
