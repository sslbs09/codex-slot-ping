using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Core.Scheduler;

namespace CodexSlotPing.Core.Tests.Configuration;

public sealed class TimezoneUpdateServiceTests
{
    [Fact]
    public async Task ChangedWindowsTimeZone_FailsClosedUntilConfirmedUpdate()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.json");
            var store = CreateStore(configPath);
            var currentZone = TimeZoneInfo.Local.Id;
            var config = AppConfigDefaults.Create() with { TimeZone = OtherTimeZone(currentZone) };
            await store.SaveAsync(config, CancellationToken.None);
            var originalBytes = await File.ReadAllBytesAsync(configPath);
            var runnerPath = Path.Combine(root, "CodexSlotPing.Runner.exe");
            var gateway = new RecordingSchedulerGateway(OwnedStatus(runnerPath));
            var service = new TimezoneUpdateService(store, gateway);

            var pending = await service.ApplyAsync(config, runnerPath, currentZone, userConfirmed: false, CancellationToken.None);

            Assert.Equal(TimezoneUpdateState.TIMEZONE_CHANGED, pending.State);
            Assert.False(pending.Succeeded);
            Assert.False(pending.ConfigSaved);
            Assert.Equal(SchedulerState.TIMEZONE_CHANGED, pending.SchedulerStatus.State);
            Assert.Equal(0, gateway.InstallCalls);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(configPath));

            var updated = await service.ApplyAsync(config, runnerPath, currentZone, userConfirmed: true, CancellationToken.None);

            Assert.Equal(TimezoneUpdateState.UPDATED, updated.State);
            Assert.True(updated.Succeeded);
            Assert.True(updated.ConfigSaved);
            Assert.Equal(SchedulerState.ON, updated.SchedulerStatus.State);
            Assert.Equal(1, gateway.InstallCalls);
            Assert.Equal(currentZone, gateway.LastDefinition!.ConfiguredTimeZoneId);
            Assert.Equal(Path.GetFullPath(runnerPath), Path.GetFullPath(gateway.LastDefinition.RunnerPath));
            Assert.Equal("\\Codex Slot Ping", gateway.LastDefinition.TaskPath);
            Assert.Equal(currentZone, (await store.LoadOrCreateAsync(CancellationToken.None)).TimeZone);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SchedulerUpdateFailure_LeavesTimezoneLockUnchangedAndNeverHealthy()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.json");
            var store = CreateStore(configPath);
            var currentZone = TimeZoneInfo.Local.Id;
            var config = AppConfigDefaults.Create() with { TimeZone = OtherTimeZone(currentZone) };
            await store.SaveAsync(config, CancellationToken.None);
            var runnerPath = Path.Combine(root, "CodexSlotPing.Runner.exe");
            var gateway = new RecordingSchedulerGateway(OwnedStatus(runnerPath)) { FailInstall = true };
            var service = new TimezoneUpdateService(store, gateway);

            var result = await service.ApplyAsync(config, runnerPath, currentZone, userConfirmed: true, CancellationToken.None);

            Assert.Equal(TimezoneUpdateState.ERROR, result.State);
            Assert.False(result.Succeeded);
            Assert.False(result.ConfigSaved);
            Assert.NotEqual(SchedulerState.ON, result.SchedulerStatus.State);
            Assert.Equal(1, gateway.InstallCalls);
            Assert.Equal(config.TimeZone, (await store.LoadOrCreateAsync(CancellationToken.None)).TimeZone);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConfirmedTimezoneUpdate_PreservesDisabledOwnedTask()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = CreateStore(Path.Combine(root, "config.json"));
            var currentZone = TimeZoneInfo.Local.Id;
            var config = AppConfigDefaults.Create() with { TimeZone = OtherTimeZone(currentZone) };
            var runnerPath = Path.Combine(root, "CodexSlotPing.Runner.exe");
            var gateway = new RecordingSchedulerGateway(OwnedStatus(runnerPath, enabled: false));
            var service = new TimezoneUpdateService(store, gateway);

            var result = await service.ApplyAsync(config, runnerPath, currentZone, userConfirmed: true, CancellationToken.None);

            Assert.Equal(TimezoneUpdateState.UPDATED, result.State);
            Assert.Equal(SchedulerState.OFF, result.SchedulerStatus.State);
            Assert.False(gateway.LastDefinition!.Enabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonConfigStore CreateStore(string path) =>
        new(new AtomicJsonFile(new PhysicalAtomicFileOperations()), path);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CodexSlotPing-TimezoneTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string OtherTimeZone(string currentZone) =>
        string.Equals(currentZone, "UTC", StringComparison.OrdinalIgnoreCase)
            ? "Pacific Standard Time"
            : "UTC";

    private static SchedulerStatus OwnedStatus(string actionPath, bool enabled = true) =>
        new("\\Codex Slot Ping", enabled ? SchedulerState.OWNED_ENABLED : SchedulerState.OWNED_DISABLED, true, true, enabled,
            "Codex Slot Ping; Owner=CodexSlotPing; Schema=1", Readback(actionPath),
            Array.Empty<string>(), null);

    private static SchedulerTaskReadback Readback(string actionPath) =>
        new(
            Array.Empty<SchedulerTriggerReadback>(),
            true, "IgnoreNew", "PT2M", 0, null, false, "S-1-test", 3, 0,
            0, actionPath, "--task-path \"\\Codex Slot Ping\"", Path.GetDirectoryName(actionPath));

    private sealed class RecordingSchedulerGateway(SchedulerStatus initialStatus) : ITaskSchedulerGateway
    {
        private SchedulerStatus _status = initialStatus;

        public bool FailInstall { get; init; }
        public int InstallCalls { get; private set; }
        public SchedulerTaskDefinition? LastDefinition { get; private set; }

        public Task<SchedulerStatus> ReadStatusAsync(
            string taskPath,
            string currentRunnerPath,
            string configuredTimeZoneId,
            CancellationToken cancellationToken) => Task.FromResult(_status);

        public Task<SchedulerOperationResult> InstallOrUpdateAsync(
            SchedulerTaskDefinition definition,
            CancellationToken cancellationToken)
        {
            InstallCalls++;
            LastDefinition = definition;
            if (FailInstall)
            {
                var failed = _status with { State = SchedulerState.ERROR, DiagnosticCategory = "scheduler-unavailable" };
                return Task.FromResult(new SchedulerOperationResult(false, "scheduler-unavailable", failed));
            }

            _status = OwnedStatus(definition.RunnerPath, definition.Enabled);
            return Task.FromResult(new SchedulerOperationResult(true, null, _status));
        }

        public Task<SchedulerOperationResult> DisableAsync(string taskPath, CancellationToken cancellationToken) =>
            Task.FromResult(new SchedulerOperationResult(false, "not-used", _status));

        public Task<SchedulerOperationResult> RemoveAsync(string taskPath, CancellationToken cancellationToken) =>
            Task.FromResult(new SchedulerOperationResult(false, "not-used", _status));
    }
}
