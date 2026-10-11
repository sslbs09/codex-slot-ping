using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
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
    }

    [Fact]
    public async Task SecondClaimForTheSameIdentity_IsRejected()
    {
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)));

        var first = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);
        var second = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Claimed, first.Kind);
        Assert.Equal(SlotClaimKind.AlreadyConsumed, second.Kind);
        Assert.Equal(FormalSlotStatus.CLAIMED, second.Status);
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
            Slots = ["08:00", "12:01", "17:01", "22:01"]
        };
        fixture.Clock.Now = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(8, 1));

        var second = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Claimed, first.Kind);
        Assert.Equal(SlotClaimKind.AlreadyConsumed, second.Kind);
        Assert.Single(fixture.StateStore.State.FormalSlots);
        Assert.Equal(new TimeOnly(7, 0), fixture.StateStore.State.FormalSlots[0].SlotId.LocalTime);
    }

    [Fact]
    public async Task EditedUnclaimedFutureIdentity_CanBeClaimedAfterOldIdentityWasMissed()
    {
        var date = new DateOnly(2026, 1, 15);
        var state = StateDocument.Empty() with
        {
            FormalSlots = [Record(date, "07:00", 1, FormalSlotStatus.CONFIRMED_SUCCESS), Record(date, "12:01", 2, FormalSlotStatus.MISSED)]
        };
        var config = AppConfigDefaults.Create() with
        {
            TimeZone = EasternTimeZoneId,
            Slots = ["07:00", "12:10", "17:01", "22:01"]
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
        var state = StateDocument.Empty() with { HighestObservedUtc = highWater };
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
        var config = AppConfigDefaults.Create() with { Enabled = false, TimeZone = EasternTimeZoneId };
        var fixture = CreateFixture(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 1)), config);

        var result = await fixture.Service.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(SlotClaimKind.Blocked, result.Kind);
        Assert.Equal(SlotClaimBlockReason.ConfigurationDisabled, result.BlockReason);
        Assert.Equal(0, fixture.Clock.ReadCount);
        Assert.Equal(0, fixture.StateStore.SaveCount);
    }

    private static ClaimFixture CreateFixture(
        DateTimeOffset nowUtc,
        AppConfig? config = null,
        StateDocument? state = null,
        bool mutexAvailable = true)
    {
        var clock = new FixedClock(nowUtc);
        var zoneProvider = new FixedTimeZoneProvider(EasternTimeZoneId, EasternTimeZone);
        var configStore = new InMemoryConfigStore(config ?? AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId });
        var mutex = new RecordingStateMutex(mutexAvailable);
        var stateStore = new InMemoryStateStore(state ?? StateDocument.Empty(), () => mutex.IsHeld);
        var evaluator = new SlotAdmissionEvaluator(zoneProvider);
        var service = new FormalSlotClaimService(clock, zoneProvider, configStore, stateStore, mutex, evaluator);
        return new ClaimFixture(service, clock, configStore, stateStore, mutex);
    }

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

    private sealed class InMemoryConfigStore(AppConfig config) : IConfigStore
    {
        public AppConfig Config { get; set; } = config;

        public int LoadCount { get; private set; }

        public Task<AppConfig> LoadOrCreateAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(Config);
        }

        public Task SaveAsync(AppConfig config, CancellationToken cancellationToken)
        {
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
        public bool IsHeld { get; private set; }

        public StateMutexLease? TryAcquire(TimeSpan timeout)
        {
            if (!available || IsHeld)
            {
                return null;
            }

            IsHeld = true;
            return new StateMutexLease(() => IsHeld = false);
        }
    }
}
