using System.Runtime.InteropServices;
using System.Security.Principal;
using CodexSlotPing.Core.Scheduler;

namespace CodexSlotPing.IntegrationTests.Scheduler;

public sealed class TaskSchedulerContractTests
{
    private const string ExpectedOwnerMarker = "Codex Slot Ping; Owner=CodexSlotPing; Schema=1";
    private readonly TaskSchedulerStaWorker _staWorker = new();

    [Fact]
    public async Task ConcurrentRunnerSchedulerRequests_AreSerializedAcrossStaWorkers()
    {
        var firstWorker = new TaskSchedulerStaWorker();
        var secondWorker = new TaskSchedulerStaWorker();
        using var firstEntered = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var secondStarted = 0;

        var first = firstWorker.RunAsync(() =>
        {
            firstEntered.Set();
            if (!releaseFirst.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release the first STA operation.");
            }
            return true;
        }, CancellationToken.None);

        Assert.True(await Task.Run(() => firstEntered.Wait(TimeSpan.FromSeconds(5))));
        var second = secondWorker.RunAsync(() =>
        {
            Interlocked.Exchange(ref secondStarted, 1);
            return true;
        }, CancellationToken.None);

        await Task.Delay(150);
        var overlapped = Volatile.Read(ref secondStarted);
        releaseFirst.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, overlapped);
        Assert.Equal(1, secondStarted);
    }

    [Fact]
    public async Task InstallReadsBackEveryFrozenTaskContractField()
    {
        var taskPath = CreateUniqueTaskPath();
        var runnerPath = FindRunnerPath();
        Assert.True(File.Exists(runnerPath), $"Runner executable was not built at {runnerPath}.");
        var definition = CreateDefinition(taskPath, runnerPath, wakeToRun: true, [new(7, 0), new(12, 1), new(17, 1), new(22, 1)]);
        var gateway = new WindowsTaskSchedulerGateway();

        try
        {
            var installed = await gateway.InstallOrUpdateAsync(definition, CancellationToken.None);

            Assert.True(installed.Succeeded, Describe(installed));
            var status = await gateway.ReadStatusAsync(taskPath, runnerPath, definition.ConfiguredTimeZoneId, CancellationToken.None);
            Assert.Equal(SchedulerState.OWNED_ENABLED, status.State);
            Assert.True(status.IsOwned);
            Assert.True(status.IsEnabled);
            Assert.Empty(status.ContractMismatches);
            Assert.Equal(ExpectedOwnerMarker, status.Description);

            var readback = Assert.IsType<SchedulerTaskReadback>(status.Readback);
            Assert.Equal(4, readback.Triggers.Count);
            Assert.All(readback.Triggers, trigger =>
            {
                Assert.True(trigger.IsDaily);
                Assert.True(trigger.Enabled);
                Assert.Equal(1, trigger.DaysInterval);
                Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}$", trigger.StartBoundary);
            });
            Assert.Equal(new[] { "07:00", "12:01", "17:01", "22:01" },
                readback.Triggers.Select(trigger => trigger.StartBoundary.Substring(11, 5)));
            Assert.False(readback.StartWhenAvailable);
            Assert.Equal("IgnoreNew", readback.MultipleInstancesPolicy);
            Assert.Equal("PT2M", readback.ExecutionTimeLimit);
            Assert.Equal(0, readback.RestartCount);
            Assert.True(string.IsNullOrEmpty(readback.RestartInterval));
            Assert.True(readback.WakeToRun);
            var currentUserSid = WindowsIdentity.GetCurrent().User!.Value;
            Assert.True(
                string.Equals(currentUserSid, readback.PrincipalUserId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(WindowsIdentity.GetCurrent().Name, readback.PrincipalUserId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(WindowsIdentity.GetCurrent().Name.Split('\\').Last(), readback.PrincipalUserId, StringComparison.OrdinalIgnoreCase),
                "Task principal must resolve to the current interactive user.");
            Assert.Equal(3, readback.LogonType);
            Assert.Equal(0, readback.RunLevel);
            Assert.Equal(Path.GetFullPath(runnerPath), readback.ActionPath);
            Assert.Equal($"--task-path \"{taskPath}\"", readback.ActionArguments);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(runnerPath)), readback.WorkingDirectory);
        }
        finally
        {
            await gateway.RemoveAsync(taskPath, CancellationToken.None);
        }
    }

    [Fact]
    public async Task OwnedTaskCanBeUpdatedDisabledAndRemoved()
    {
        var taskPath = CreateUniqueTaskPath();
        var runnerPath = FindRunnerPath();
        var gateway = new WindowsTaskSchedulerGateway();
        var initial = CreateDefinition(taskPath, runnerPath, wakeToRun: true, [new(1, 0), new(4, 0), new(12, 0), new(20, 0)]);
        var updated = CreateDefinition(taskPath, runnerPath, wakeToRun: false, [new(2, 0), new(5, 0), new(13, 0), new(21, 0)]);

        try
        {
            var installed = await gateway.InstallOrUpdateAsync(initial, CancellationToken.None);
            Assert.True(installed.Succeeded, Describe(installed));
            var updatedResult = await gateway.InstallOrUpdateAsync(updated, CancellationToken.None);
            Assert.True(updatedResult.Succeeded, Describe(updatedResult));
            var updatedStatus = await gateway.ReadStatusAsync(taskPath, runnerPath, updated.ConfiguredTimeZoneId, CancellationToken.None);
            Assert.True(updatedStatus.IsEnabled);
            Assert.False(updatedStatus.Readback!.WakeToRun);
            Assert.Equal(new[] { "02:00", "05:00", "13:00", "21:00" },
                updatedStatus.Readback.Triggers.Select(trigger => trigger.StartBoundary.Substring(11, 5)));

            var disabled = await gateway.DisableAsync(taskPath, CancellationToken.None);
            Assert.True(disabled.Succeeded, disabled.DiagnosticCategory);
            var disabledStatus = await gateway.ReadStatusAsync(taskPath, runnerPath, updated.ConfiguredTimeZoneId, CancellationToken.None);
            Assert.Equal(SchedulerState.OWNED_DISABLED, disabledStatus.State);
            Assert.True(disabledStatus.IsOwned);
            Assert.False(disabledStatus.IsEnabled);

            var changedWhileDisabled = updated with { Enabled = false, WakeToRun = true };
            var disabledUpdate = await gateway.InstallOrUpdateAsync(changedWhileDisabled, CancellationToken.None);
            Assert.True(disabledUpdate.Succeeded, Describe(disabledUpdate));
            var preservedDisabledStatus = await gateway.ReadStatusAsync(taskPath, runnerPath, updated.ConfiguredTimeZoneId, CancellationToken.None);
            Assert.Equal(SchedulerState.OWNED_DISABLED, preservedDisabledStatus.State);
            Assert.False(preservedDisabledStatus.IsEnabled);
            Assert.True(preservedDisabledStatus.Readback!.WakeToRun);

            var removed = await gateway.RemoveAsync(taskPath, CancellationToken.None);
            Assert.True(removed.Succeeded, removed.DiagnosticCategory);
            var removedStatus = await gateway.ReadStatusAsync(taskPath, runnerPath, updated.ConfiguredTimeZoneId, CancellationToken.None);
            Assert.Equal(SchedulerState.NOT_INSTALLED, removedStatus.State);
            Assert.False(removedStatus.Exists);
        }
        finally
        {
            await gateway.RemoveAsync(taskPath, CancellationToken.None);
        }
    }

    [Fact]
    public async Task MovedRunnerPath_ShowsNeedsRepair_AndRepairUpdatesOwnedAction()
    {
        var taskPath = CreateUniqueTaskPath();
        var currentRunnerPath = FindRunnerPath();
        var oldRunnerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        Assert.True(File.Exists(oldRunnerPath), $"The old action fixture was not found at {oldRunnerPath}.");
        var gateway = new WindowsTaskSchedulerGateway();
        var definition = CreateDefinition(taskPath, oldRunnerPath, wakeToRun: false, [new(7, 0), new(12, 1), new(17, 1), new(22, 1)]);
        var evaluator = new SchedulerHealthEvaluator();

        try
        {
            var installedAtOldPath = await gateway.InstallOrUpdateAsync(definition, CancellationToken.None);
            Assert.True(installedAtOldPath.Succeeded, Describe(installedAtOldPath));

            var staleStatus = await gateway.ReadStatusAsync(taskPath, currentRunnerPath, definition.ConfiguredTimeZoneId, CancellationToken.None);
            var health = evaluator.Evaluate(staleStatus, currentRunnerPath, definition.ConfiguredTimeZoneId, TimeZoneInfo.Local.Id);
            Assert.Equal(SchedulerState.NEEDS_REPAIR, health.State);
            Assert.Equal(Path.GetFullPath(oldRunnerPath), health.RegisteredRunnerPath);
            Assert.Equal(Path.GetFullPath(currentRunnerPath), health.ExpectedRunnerPath);

            var repairedDefinition = definition with { RunnerPath = currentRunnerPath };
            var repaired = await gateway.InstallOrUpdateAsync(repairedDefinition, CancellationToken.None);
            Assert.True(repaired.Succeeded, Describe(repaired));

            var verified = await gateway.ReadStatusAsync(taskPath, currentRunnerPath, definition.ConfiguredTimeZoneId, CancellationToken.None);
            var verifiedHealth = evaluator.Evaluate(verified, currentRunnerPath, definition.ConfiguredTimeZoneId, TimeZoneInfo.Local.Id);
            Assert.Equal(SchedulerState.ON, verifiedHealth.State);
            Assert.Equal(Path.GetFullPath(currentRunnerPath), verifiedHealth.RegisteredRunnerPath);
        }
        finally
        {
            await gateway.RemoveAsync(taskPath, CancellationToken.None);
        }
    }

    [Fact]
    public async Task SameNameTaskWithoutOwnerMarkerIsByteForByteUntouched()
    {
        var taskPath = CreateUniqueTaskPath();
        var runnerPath = FindRunnerPath();
        var originalXml = await CreateUnownedTaskAsync(taskPath, runnerPath);
        var gateway = new WindowsTaskSchedulerGateway();
        var definition = CreateDefinition(taskPath, runnerPath, wakeToRun: true, [new(1, 0), new(4, 0), new(12, 0), new(20, 0)]);

        try
        {
            var install = await gateway.InstallOrUpdateAsync(definition, CancellationToken.None);
            var disable = await gateway.DisableAsync(taskPath, CancellationToken.None);
            var remove = await gateway.RemoveAsync(taskPath, CancellationToken.None);

            Assert.False(install.Succeeded);
            Assert.False(disable.Succeeded);
            Assert.False(remove.Succeeded);
            Assert.Equal("task-ownership-conflict", install.DiagnosticCategory);
            Assert.Equal("task-ownership-conflict", disable.DiagnosticCategory);
            Assert.Equal("task-ownership-conflict", remove.DiagnosticCategory);
            Assert.Equal(originalXml, await ReadTaskXmlAsync(taskPath));
            var status = await gateway.ReadStatusAsync(taskPath, runnerPath, definition.ConfiguredTimeZoneId, CancellationToken.None);
            Assert.Equal(SchedulerState.CONFLICT, status.State);
        }
        finally
        {
            await DeleteTaskAsync(taskPath);
        }
    }

    [Fact]
    public async Task OwnedTaskWithExtraAction_IsMarkedDrifted()
    {
        var taskPath = CreateUniqueTaskPath();
        var runnerPath = FindRunnerPath();
        var gateway = new WindowsTaskSchedulerGateway();
        var definition = CreateDefinition(taskPath, runnerPath, wakeToRun: false,
            [new(7, 0), new(12, 1), new(17, 1), new(22, 1)]);

        try
        {
            var installed = await gateway.InstallOrUpdateAsync(definition, CancellationToken.None);
            Assert.True(installed.Succeeded, Describe(installed));
            await AddExtraActionAsync(taskPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));

            var status = await gateway.ReadStatusAsync(
                taskPath, runnerPath, definition.ConfiguredTimeZoneId, CancellationToken.None);

            Assert.Equal(SchedulerState.DRIFTED, status.State);
            Assert.Contains("action-count-mismatch", status.ContractMismatches);
        }
        finally
        {
            await gateway.RemoveAsync(taskPath, CancellationToken.None);
        }
    }

    private static SchedulerTaskDefinition CreateDefinition(
        string taskPath,
        string runnerPath,
        bool wakeToRun,
        IReadOnlyList<TimeOnly> slotTimes) =>
        new(taskPath, runnerPath, TimeZoneInfo.Local.Id, slotTimes, wakeToRun, new DateOnly(2099, 1, 1));

    private static string CreateUniqueTaskPath()
    {
        var taskPath = $"\\Codex Slot Ping Integration {Guid.NewGuid():N}";
        Assert.StartsWith("\\Codex Slot Ping Integration ", taskPath, StringComparison.Ordinal);
        Assert.NotEqual("\\Codex Slot Ping", taskPath);
        return taskPath;
    }

    private static string Describe(SchedulerOperationResult result)
    {
        var status = result.Status;
        var readback = status?.Readback;
        var principal = readback?.PrincipalUserId;
        var currentSid = WindowsIdentity.GetCurrent().User!.Value;
        var currentName = WindowsIdentity.GetCurrent().Name;
        return $"{result.DiagnosticCategory}; state={status?.State}; mismatches={string.Join(',', status?.ContractMismatches ?? Array.Empty<string>())}; " +
               $"wake={readback?.WakeToRun}; triggerCount={readback?.Triggers.Count}; " +
               $"times={string.Join(',', readback?.Triggers.Select(trigger => trigger.StartBoundary.Substring(11, 5)) ?? Array.Empty<string>())}; " +
               $"limit={readback?.ExecutionTimeLimit}; instances={readback?.MultipleInstancesPolicy}; restart={readback?.RestartCount}/{readback?.RestartInterval}; " +
               $"logon={readback?.LogonType}; runLevel={readback?.RunLevel}; actionType={readback?.ActionType}; " +
               $"principalPresent={!string.IsNullOrWhiteSpace(principal)}; principalLength={principal?.Length}; " +
               $"principalMatchesSid={string.Equals(principal, currentSid, StringComparison.OrdinalIgnoreCase)}; " +
               $"principalMatchesName={string.Equals(principal, currentName, StringComparison.OrdinalIgnoreCase)}";
    }

    private static string FindRunnerPath()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodexSlotPing.sln")))
        {
            repository = repository.Parent;
        }

        if (repository is null)
        {
            throw new DirectoryNotFoundException("Could not locate the solution root for the Task Scheduler integration test.");
        }

        return Path.Combine(repository.FullName, "src", "CodexSlotPing.Runner", "bin", "Release", "net10.0-windows", "CodexSlotPing.Runner.exe");
    }

    private async Task<string> CreateUnownedTaskAsync(string taskPath, string runnerPath) =>
        await _staWorker.RunAsync(() =>
        {
            dynamic service = CreateTaskService();
            dynamic root = service.GetFolder("\\");
            dynamic definition = service.NewTask(0);
            dynamic registration = definition.RegistrationInfo;
            registration.Description = "Third-party test task; preserve this definition.";
            dynamic settings = definition.Settings;
            settings.Enabled = true;
            dynamic principal = definition.Principal;
            var userSid = WindowsIdentity.GetCurrent().User!.Value;
            principal.UserId = userSid;
            principal.LogonType = 3;
            principal.RunLevel = 0;
            dynamic action = definition.Actions.Create(0);
            action.Path = Path.GetFullPath(runnerPath);
            action.Arguments = "--unowned-task-test";
            action.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(runnerPath));
            dynamic registered = root.RegisterTaskDefinition(taskPath.TrimStart('\\'), definition, 6, userSid, null, 3, null);
            registered.Enabled = true;
            var xml = (string)registered.Xml;
            ReleaseComObject(registered);
            ReleaseComObject(action);
            ReleaseComObject(principal);
            ReleaseComObject(settings);
            ReleaseComObject(registration);
            ReleaseComObject(definition);
            ReleaseComObject(root);
            ReleaseComObject(service);
            return xml;
        }, CancellationToken.None);

    private async Task<string> ReadTaskXmlAsync(string taskPath) =>
        await _staWorker.RunAsync(() =>
        {
            dynamic service = CreateTaskService();
            dynamic root = service.GetFolder("\\");
            dynamic task = root.GetTask(taskPath.TrimStart('\\'));
            var xml = (string)task.Xml;
            ReleaseComObject(task);
            ReleaseComObject(root);
            ReleaseComObject(service);
            return xml;
        }, CancellationToken.None);

    private async Task AddExtraActionAsync(string taskPath, string additionalExecutable) =>
        await _staWorker.RunAsync(() =>
        {
            object? service = null;
            object? root = null;
            object? task = null;
            object? definition = null;
            object? actions = null;
            object? action = null;
            object? registered = null;
            try
            {
                service = CreateTaskService();
                dynamic serviceObject = service;
                root = serviceObject.GetFolder("\\");
                dynamic rootObject = root;
                task = rootObject.GetTask(taskPath.TrimStart('\\'));
                dynamic taskObject = task;
                definition = taskObject.Definition;
                dynamic definitionObject = definition;
                actions = definitionObject.Actions;
                dynamic actionsObject = actions;
                action = actionsObject.Create(0);
                dynamic actionObject = action;
                actionObject.Path = additionalExecutable;
                actionObject.Arguments = "/c exit 0";
                actionObject.WorkingDirectory = Path.GetDirectoryName(additionalExecutable);
                var userSid = WindowsIdentity.GetCurrent().User!.Value;
                registered = rootObject.RegisterTaskDefinition(
                    taskPath.TrimStart('\\'), definitionObject, 6, userSid, null, 3, null);
                return true;
            }
            finally
            {
                ReleaseComObject(registered!);
                ReleaseComObject(action!);
                ReleaseComObject(actions!);
                ReleaseComObject(definition!);
                ReleaseComObject(task!);
                ReleaseComObject(root!);
                ReleaseComObject(service!);
            }
        }, CancellationToken.None);

    private async Task DeleteTaskAsync(string taskPath) =>
        await _staWorker.RunAsync(() =>
        {
            dynamic service = CreateTaskService();
            dynamic root = service.GetFolder("\\");
            root.DeleteTask(taskPath.TrimStart('\\'), 0);
            ReleaseComObject(root);
            ReleaseComObject(service);
            return true;
        }, CancellationToken.None);

    private static object CreateTaskService()
    {
        var taskServiceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(taskServiceType)!;
        service.Connect();
        return service;
    }

    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

}
