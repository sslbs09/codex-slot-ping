using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace CodexSlotPing.Core.Scheduler;

[SupportedOSPlatform("windows")]
public sealed class WindowsTaskSchedulerGateway : ITaskSchedulerGateway
{
    public const string ProductionTaskPath = SchedulerTaskContract.ProductionTaskPath;
    public const string OwnerMarker = SchedulerTaskContract.OwnerMarker;

    private const int TaskTriggerTime = 1;
    private const int TaskTriggerDaily = 2;
    private const int TaskActionExec = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelLeastPrivilege = 0;
    private const int TaskMultipleInstancesIgnoreNew = 2;
    private readonly TaskSchedulerStaWorker _staWorker;

    public WindowsTaskSchedulerGateway(TaskSchedulerStaWorker? staWorker = null)
    {
        _staWorker = staWorker ?? new TaskSchedulerStaWorker();
    }

    public async Task<SchedulerStatus> ReadStatusAsync(
        string taskPath,
        string currentRunnerPath,
        string configuredTimeZoneId,
        CancellationToken cancellationToken)
    {
        if (!TryGetTaskName(taskPath, out var taskName))
        {
            return ErrorStatus(taskPath, "invalid-task-path");
        }

        string runnerPath;
        try
        {
            runnerPath = Path.GetFullPath(currentRunnerPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ErrorStatus(taskPath, "invalid-runner-path");
        }

        try
        {
            return await _staWorker.RunAsync(
                () => ReadStatusOnSta(taskPath, taskName, runnerPath, configuredTimeZoneId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsSchedulerException(exception))
        {
            return ErrorStatus(taskPath, "scheduler-unavailable");
        }
    }

    public async Task<SchedulerOperationResult> InstallOrUpdateAsync(
        SchedulerTaskDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var validation = ValidateDefinition(definition);
        if (validation is not null)
        {
            return Failure(validation);
        }

        var taskName = definition.TaskPath.TrimStart('\\');
        var runnerPath = Path.GetFullPath(definition.RunnerPath);
        try
        {
            return await _staWorker.RunAsync(
                () => InstallOrUpdateOnSta(definition, taskName, runnerPath),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsSchedulerException(exception))
        {
            return Failure("scheduler-unavailable");
        }
    }

    public async Task<SchedulerOperationResult> DisableAsync(string taskPath, CancellationToken cancellationToken)
    {
        if (!TryGetTaskName(taskPath, out var taskName))
        {
            return Failure("invalid-task-path");
        }

        try
        {
            return await _staWorker.RunAsync(() => DisableOnSta(taskPath, taskName), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsSchedulerException(exception))
        {
            return Failure("scheduler-unavailable");
        }
    }

    public async Task<SchedulerOperationResult> RemoveAsync(string taskPath, CancellationToken cancellationToken)
    {
        if (!TryGetTaskName(taskPath, out var taskName))
        {
            return Failure("invalid-task-path");
        }

        try
        {
            return await _staWorker.RunAsync(() => RemoveOnSta(taskPath, taskName), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsSchedulerException(exception))
        {
            return Failure("scheduler-unavailable");
        }
    }

    private static SchedulerStatus ReadStatusOnSta(
        string taskPath,
        string taskName,
        string currentRunnerPath,
        string configuredTimeZoneId)
    {
        object? service = null;
        object? rootFolder = null;
        object? registeredTask = null;
        try
        {
            service = CreateTaskService();
            dynamic serviceObject = service;
            rootFolder = serviceObject.GetFolder("\\");
            dynamic rootObject = rootFolder;
            registeredTask = FindTask(rootObject, taskName);
            if (registeredTask is null)
            {
                return new SchedulerStatus(taskPath, SchedulerState.NOT_INSTALLED, false, false, false, null, null,
                    Array.Empty<string>(), null);
            }

            dynamic taskObject = registeredTask;
            var isEnabled = (bool)taskObject.Enabled;
            object? definition = taskObject.Definition;
            object? registrationInfo = null;
            try
            {
                dynamic definitionObject = definition!;
                registrationInfo = definitionObject.RegistrationInfo;
                dynamic registrationObject = registrationInfo;
                var description = (string?)registrationObject.Description ?? string.Empty;
                if (!description.Contains(OwnerMarker, StringComparison.Ordinal))
                {
                    return new SchedulerStatus(taskPath, SchedulerState.CONFLICT, true, false, isEnabled,
                        description, null, ["task-ownership-conflict"], "task-ownership-conflict");
                }

                var readback = ReadTaskDefinition(definitionObject);
                var mismatches = GetContractMismatches(taskPath, currentRunnerPath, configuredTimeZoneId,
                    WindowsIdentitySid(), description, readback);
                var state = mismatches.Count > 0
                    ? SchedulerState.DRIFTED
                    : isEnabled ? SchedulerState.OWNED_ENABLED : SchedulerState.OWNED_DISABLED;
                var diagnosticCategory = mismatches.Count == 0 ? null : mismatches[0];
                return new SchedulerStatus(taskPath, state, true, true, isEnabled, description,
                    readback, mismatches, diagnosticCategory);
            }
            finally
            {
                ReleaseComObject(registrationInfo);
                ReleaseComObject(definition);
            }
        }
        finally
        {
            ReleaseComObject(registeredTask);
            ReleaseComObject(rootFolder);
            ReleaseComObject(service);
        }
    }

    private static SchedulerOperationResult InstallOrUpdateOnSta(
        SchedulerTaskDefinition definition,
        string taskName,
        string runnerPath)
    {
        object? service = null;
        object? rootFolder = null;
        object? existingTask = null;
        object? taskDefinition = null;
        object? registeredTask = null;
        try
        {
            service = CreateTaskService();
            dynamic serviceObject = service;
            rootFolder = serviceObject.GetFolder("\\");
            dynamic rootObject = rootFolder;
            existingTask = FindTask(rootObject, taskName);
            if (existingTask is not null)
            {
                dynamic existingObject = existingTask;
                object? existingDefinition = existingObject.Definition;
                object? registrationInfo = null;
                try
                {
                    dynamic definitionObject = existingDefinition!;
                    registrationInfo = definitionObject.RegistrationInfo;
                    dynamic registrationObject = registrationInfo;
                    var description = (string?)registrationObject.Description ?? string.Empty;
                    if (!description.Contains(OwnerMarker, StringComparison.Ordinal))
                    {
                        var conflictStatus = new SchedulerStatus(definition.TaskPath, SchedulerState.CONFLICT,
                            true, false, (bool)existingObject.Enabled, description, null,
                            ["task-ownership-conflict"], "task-ownership-conflict");
                        return new SchedulerOperationResult(false, "task-ownership-conflict", conflictStatus);
                    }
                }
                finally
                {
                    ReleaseComObject(registrationInfo);
                    ReleaseComObject(existingDefinition);
                }
            }

            taskDefinition = CreateTaskDefinition(definition, taskName, runnerPath, WindowsIdentitySid());
            dynamic definitionToRegister = taskDefinition;
            registeredTask = rootObject.RegisterTaskDefinition(
                taskName,
                definitionToRegister,
                TaskCreateOrUpdate,
                WindowsIdentitySid(),
                null,
                TaskLogonInteractiveToken,
                null);
            dynamic registeredObject = registeredTask;
            registeredObject.Enabled = definition.Enabled;

            var status = ReadStatusOnSta(definition.TaskPath, taskName, runnerPath, definition.ConfiguredTimeZoneId);
            if (MatchesDefinition(status, definition))
            {
                return new SchedulerOperationResult(true, null, status);
            }

            registeredObject.Enabled = false;
            var safeStatus = ReadStatusOnSta(definition.TaskPath, taskName, runnerPath, definition.ConfiguredTimeZoneId);
            return new SchedulerOperationResult(false, "readback-verification-failed", safeStatus);
        }
        finally
        {
            ReleaseComObject(registeredTask);
            ReleaseComObject(taskDefinition);
            ReleaseComObject(existingTask);
            ReleaseComObject(rootFolder);
            ReleaseComObject(service);
        }
    }

    private static SchedulerOperationResult DisableOnSta(string taskPath, string taskName)
    {
        object? service = null;
        object? rootFolder = null;
        object? registeredTask = null;
        try
        {
            service = CreateTaskService();
            dynamic serviceObject = service;
            rootFolder = serviceObject.GetFolder("\\");
            dynamic rootObject = rootFolder;
            registeredTask = FindTask(rootObject, taskName);
            if (registeredTask is null)
            {
                var absent = new SchedulerStatus(taskPath, SchedulerState.NOT_INSTALLED, false, false, false,
                    null, null, Array.Empty<string>(), null);
                return new SchedulerOperationResult(true, null, absent);
            }

            dynamic registeredObject = registeredTask;
            if (!IsOwned(registeredObject))
            {
                var conflict = new SchedulerStatus(taskPath, SchedulerState.CONFLICT, true, false,
                    (bool)registeredObject.Enabled, GetDescription(registeredObject), null,
                    new[] { "task-ownership-conflict" }, "task-ownership-conflict");
                return new SchedulerOperationResult(false, "task-ownership-conflict", conflict);
            }

            registeredObject.Enabled = false;
            var readback = ReadTaskDefinitionForTask(registeredObject);
            var currentRunnerPath = readback.ActionPath ?? string.Empty;
            var status = ReadStatusOnSta(taskPath, taskName, currentRunnerPath, "SYSTEM");
            var succeeded = status.IsOwned && !status.IsEnabled;
            return new SchedulerOperationResult(succeeded, succeeded ? null : "readback-verification-failed", status);
        }
        finally
        {
            ReleaseComObject(registeredTask);
            ReleaseComObject(rootFolder);
            ReleaseComObject(service);
        }
    }

    private static SchedulerOperationResult RemoveOnSta(string taskPath, string taskName)
    {
        object? service = null;
        object? rootFolder = null;
        object? registeredTask = null;
        try
        {
            service = CreateTaskService();
            dynamic serviceObject = service;
            rootFolder = serviceObject.GetFolder("\\");
            dynamic rootObject = rootFolder;
            registeredTask = FindTask(rootObject, taskName);
            if (registeredTask is null)
            {
                var absent = new SchedulerStatus(taskPath, SchedulerState.NOT_INSTALLED, false, false, false,
                    null, null, Array.Empty<string>(), null);
                return new SchedulerOperationResult(true, null, absent);
            }

            dynamic registeredObject = registeredTask;
            if (!IsOwned(registeredObject))
            {
                var conflict = new SchedulerStatus(taskPath, SchedulerState.CONFLICT, true, false,
                    (bool)registeredObject.Enabled, GetDescription(registeredObject), null,
                    new[] { "task-ownership-conflict" }, "task-ownership-conflict");
                return new SchedulerOperationResult(false, "task-ownership-conflict", conflict);
            }

            rootObject.DeleteTask(taskName, 0);
            var remainingTask = FindTask(rootObject, taskName);
            var removed = remainingTask is null;
            ReleaseComObject(remainingTask);
            var status = new SchedulerStatus(taskPath,
                removed ? SchedulerState.NOT_INSTALLED : SchedulerState.ERROR,
                !removed, false, false, null, null, Array.Empty<string>(), removed ? null : "remove-verification-failed");
            return new SchedulerOperationResult(removed, removed ? null : "remove-verification-failed", status);
        }
        finally
        {
            ReleaseComObject(registeredTask);
            ReleaseComObject(rootFolder);
            ReleaseComObject(service);
        }
    }

    private static object CreateTaskDefinition(
        SchedulerTaskDefinition source,
        string taskName,
        string runnerPath,
        string userSid)
    {
        object? service = null;
        object? definition = null;
        object? registrationInfo = null;
        object? settings = null;
        object? principal = null;
        object? triggers = null;
        object? actions = null;
        object? action = null;
        try
        {
            service = CreateTaskService();
            dynamic serviceObject = service;
            definition = serviceObject.NewTask(0);
            dynamic definitionObject = definition;
            registrationInfo = definitionObject.RegistrationInfo;
            dynamic registrationObject = registrationInfo;
            registrationObject.Description = OwnerMarker;
            registrationObject.Author = "Codex Slot Ping";

            settings = definitionObject.Settings;
            dynamic settingsObject = settings;
            settingsObject.Enabled = source.Enabled;
            settingsObject.StartWhenAvailable = true;
            settingsObject.MultipleInstances = TaskMultipleInstancesIgnoreNew;
            settingsObject.ExecutionTimeLimit = "PT2M";
            settingsObject.RestartCount = 0;
            settingsObject.WakeToRun = source.WakeToRun;

            principal = definitionObject.Principal;
            dynamic principalObject = principal;
            principalObject.UserId = userSid;
            principalObject.LogonType = TaskLogonInteractiveToken;
            principalObject.RunLevel = TaskRunLevelLeastPrivilege;

            triggers = definitionObject.Triggers;
            dynamic triggersObject = triggers;
            if (source.SchedulePlan.OneShotTriggerAtUtc is { } triggerAtUtc)
            {
                object trigger = triggersObject.Create(TaskTriggerTime);
                dynamic triggerObject = trigger;
                triggerObject.Enabled = true;
                var localStart = triggerAtUtc.ToLocalTime();
                var localEnd = triggerAtUtc.AddDays(3650).ToLocalTime();
                triggerObject.StartBoundary = localStart.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                triggerObject.EndBoundary = localEnd.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                ReleaseComObject(trigger);
            }

            actions = definitionObject.Actions;
            dynamic actionsObject = actions;
            action = actionsObject.Create(TaskActionExec);
            dynamic actionObject = action;
            actionObject.Path = runnerPath;
            actionObject.Arguments = $"--task-path \"{source.TaskPath}\"";
            actionObject.WorkingDirectory = Path.GetDirectoryName(runnerPath);
            return definition;
        }
        catch
        {
            // The finally block releases all child objects. Release the task
            // definition here because it is transferred to the caller only
            // on the successful return path.
            ReleaseComObject(definition);
            throw;
        }
        finally
        {
            ReleaseComObject(action);
            ReleaseComObject(actions);
            ReleaseComObject(triggers);
            ReleaseComObject(principal);
            ReleaseComObject(settings);
            ReleaseComObject(registrationInfo);
            ReleaseComObject(service);
        }
    }

    private static SchedulerTaskReadback ReadTaskDefinition(dynamic definition)
    {
        object? settings = null;
        object? principal = null;
        object? triggers = null;
        object? actions = null;
        object? action = null;
        try
        {
            settings = definition.Settings;
            principal = definition.Principal;
            triggers = definition.Triggers;
            actions = definition.Actions;
            dynamic settingsObject = settings;
            dynamic principalObject = principal;
            dynamic triggersObject = triggers;
            dynamic actionsObject = actions;

            var triggerReadback = new List<SchedulerTriggerReadback>();
            var triggerCount = (int)triggersObject.Count;
            var actionCount = (int)actionsObject.Count;
            for (var index = 1; index <= triggerCount; index++)
            {
                object? trigger = null;
                try
                {
                    trigger = triggersObject.Item(index);
                    dynamic triggerObject = trigger;
                    var triggerType = (int)triggerObject.Type;
                    triggerReadback.Add(new SchedulerTriggerReadback(
                        triggerType,
                        triggerType == TaskTriggerDaily,
                        (bool)triggerObject.Enabled,
                        triggerType == TaskTriggerDaily ? (int)triggerObject.DaysInterval : 0,
                        (string)triggerObject.StartBoundary,
                        triggerType == TaskTriggerTime ? (string?)triggerObject.EndBoundary : null));
                }
                finally
                {
                    ReleaseComObject(trigger);
                }
            }

            var actionType = -1;
            string? actionPath = null;
            string? actionArguments = null;
            string? workingDirectory = null;
            if (actionCount > 0)
            {
                action = actionsObject.Item(1);
                dynamic actionObject = action;
                actionType = (int)actionObject.Type;
                if (actionType == TaskActionExec)
                {
                    actionPath = (string?)actionObject.Path;
                    actionArguments = (string?)actionObject.Arguments;
                    workingDirectory = (string?)actionObject.WorkingDirectory;
                }
            }

            return new SchedulerTaskReadback(
                triggerReadback,
                (bool)settingsObject.StartWhenAvailable,
                MultipleInstancesName((int)settingsObject.MultipleInstances),
                (string?)settingsObject.ExecutionTimeLimit ?? string.Empty,
                (int)settingsObject.RestartCount,
                (string?)settingsObject.RestartInterval,
                (bool)settingsObject.WakeToRun,
                (string?)principalObject.UserId,
                (int)principalObject.LogonType,
                (int)principalObject.RunLevel,
                actionType,
                actionPath,
                actionArguments,
                workingDirectory,
                actionCount);
        }
        finally
        {
            ReleaseComObject(action);
            ReleaseComObject(actions);
            ReleaseComObject(triggers);
            ReleaseComObject(principal);
            ReleaseComObject(settings);
        }
    }

    private static IReadOnlyList<string> GetContractMismatches(
        string taskPath,
        string currentRunnerPath,
        string configuredTimeZoneId,
        string currentUserSid,
        string description,
        SchedulerTaskReadback readback)
    {
        var mismatches = new List<string>();
        if (!description.Contains(OwnerMarker, StringComparison.Ordinal)) mismatches.Add("owner-marker-mismatch");
        if (!PathsEqual(readback.ActionPath, currentRunnerPath) || readback.ActionType != TaskActionExec)
            mismatches.Add("runner-action-mismatch");
        if (!string.Equals(readback.ActionArguments, $"--task-path \"{taskPath}\"", StringComparison.Ordinal)) mismatches.Add("task-path-argument-mismatch");
        if (!PathsEqual(readback.WorkingDirectory, Path.GetDirectoryName(currentRunnerPath)))
            mismatches.Add("working-directory-mismatch");
        if (readback.ActionCount != 1) mismatches.Add("action-count-mismatch");
        if (readback.Triggers.Count > 1 || readback.Triggers.Any(trigger =>
            !trigger.IsOneShot || !trigger.Enabled ||
            !TryParseBoundary(trigger.StartBoundary, out var start) ||
            !TryParseBoundary(trigger.EndBoundary, out var end) || end <= start))
            mismatches.Add("trigger-contract-mismatch");
        if (!readback.StartWhenAvailable) mismatches.Add("start-when-available-disabled");
        if (readback.MultipleInstancesPolicy != "IgnoreNew") mismatches.Add("multiple-instance-policy-mismatch");
        if (!string.Equals(readback.ExecutionTimeLimit, "PT2M", StringComparison.OrdinalIgnoreCase)) mismatches.Add("execution-limit-mismatch");
        if (readback.RestartCount != 0 || !string.IsNullOrEmpty(readback.RestartInterval)) mismatches.Add("restart-on-failure-enabled");
        if (!IsCurrentUserPrincipal(readback.PrincipalUserId, currentUserSid))
            mismatches.Add("principal-user-mismatch");
        if (readback.LogonType != TaskLogonInteractiveToken) mismatches.Add("principal-logon-type-mismatch");
        if (readback.RunLevel != TaskRunLevelLeastPrivilege) mismatches.Add("principal-run-level-mismatch");
        if (!IsTimeZoneCompatible(configuredTimeZoneId)) mismatches.Add("timezone-mismatch");
        return mismatches;
    }

    private static bool MatchesDefinition(SchedulerStatus status, SchedulerTaskDefinition definition)
    {
        var expectedState = definition.Enabled ? SchedulerState.OWNED_ENABLED : SchedulerState.OWNED_DISABLED;
        if (status.State != expectedState || status.IsEnabled != definition.Enabled ||
            status.Readback is null || status.ContractMismatches.Count > 0)
        {
            return false;
        }

        if (status.Readback.WakeToRun != definition.WakeToRun)
        {
            return false;
        }

        var expectedAt = definition.SchedulePlan.OneShotTriggerAtUtc;
        if (expectedAt is null)
        {
            return status.Readback.Triggers.Count == 0;
        }

        return status.Readback.Triggers.Count == 1 &&
               TryParseBoundary(status.Readback.Triggers[0].StartBoundary, out var actualAt) &&
               actualAt.ToUniversalTime() == expectedAt.Value.ToUniversalTime();
    }

    private static SchedulerTaskReadback ReadTaskDefinitionForTask(dynamic task)
    {
        object? definition = null;
        try
        {
            definition = task.Definition;
            return ReadTaskDefinition((dynamic)definition);
        }
        finally
        {
            ReleaseComObject(definition);
        }
    }

    private static bool IsOwned(dynamic task)
    {
        object? definition = null;
        object? registrationInfo = null;
        try
        {
            definition = task.Definition;
            dynamic definitionObject = definition;
            registrationInfo = definitionObject.RegistrationInfo;
            dynamic registrationObject = registrationInfo;
            var description = (string?)registrationObject.Description ?? string.Empty;
            return description.Contains(OwnerMarker, StringComparison.Ordinal);
        }
        finally
        {
            ReleaseComObject(registrationInfo);
            ReleaseComObject(definition);
        }
    }

    private static string GetDescription(dynamic task)
    {
        object? definition = null;
        object? registrationInfo = null;
        try
        {
            definition = task.Definition;
            dynamic definitionObject = definition;
            registrationInfo = definitionObject.RegistrationInfo;
            dynamic registrationObject = registrationInfo;
            return (string?)registrationObject.Description ?? string.Empty;
        }
        finally
        {
            ReleaseComObject(registrationInfo);
            ReleaseComObject(definition);
        }
    }

    private static object? FindTask(dynamic rootFolder, string taskName)
    {
        object? tasks = null;
        try
        {
            tasks = rootFolder.GetTasks(1);
            dynamic tasksObject = tasks;
            var count = (int)tasksObject.Count;
            for (var index = 1; index <= count; index++)
            {
                object? task = null;
                try
                {
                    task = tasksObject.Item(index);
                    dynamic taskObject = task;
                    if (string.Equals((string)taskObject.Name, taskName, StringComparison.OrdinalIgnoreCase))
                    {
                        return task;
                    }
                }
                catch
                {
                    ReleaseComObject(task);
                    throw;
                }

                ReleaseComObject(task);
            }

            return null;
        }
        finally
        {
            ReleaseComObject(tasks);
        }
    }

    private static object CreateTaskService()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(serviceType)!;
        service.Connect(null, null, null, null);
        return service;
    }

    private static string WindowsIdentitySid() =>
        System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The current Windows user SID could not be read.");

    private static bool TryGetTaskName(string taskPath, out string taskName)
    {
        taskName = string.Empty;
        if (string.IsNullOrWhiteSpace(taskPath) || !taskPath.StartsWith('\\') || taskPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = taskPath[1..];
        if (candidate.Length == 0 || candidate.Contains('\\') || candidate.Contains('/') ||
            candidate.Any(character => !(char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.')))
        {
            return false;
        }

        taskName = candidate;
        return true;
    }

    private static string? ValidateDefinition(SchedulerTaskDefinition definition)
    {
        if (!TryGetTaskName(definition.TaskPath, out _)) return "invalid-task-path";
        if (string.IsNullOrWhiteSpace(definition.RunnerPath) || !Path.IsPathRooted(definition.RunnerPath) ||
            !definition.RunnerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(definition.RunnerPath))
            return "invalid-runner-path";
        if (!IsTimeZoneCompatible(definition.ConfiguredTimeZoneId)) return "timezone-mismatch";
        if (definition.SchedulePlan is null)
            return "invalid-schedule-plan";
        return null;
    }

    private static bool IsTimeZoneCompatible(string configuredTimeZoneId) =>
        string.Equals(configuredTimeZoneId, "SYSTEM", StringComparison.Ordinal) ||
        string.Equals(configuredTimeZoneId, TimeZoneInfo.Local.Id, StringComparison.Ordinal);

    private static bool PathsEqual(string? actualPath, string? expectedPath)
    {
        if (string.IsNullOrWhiteSpace(actualPath) || string.IsNullOrWhiteSpace(expectedPath))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsCurrentUserPrincipal(string? principalUserId, string currentUserSid)
    {
        if (string.IsNullOrWhiteSpace(principalUserId))
        {
            return false;
        }

        var currentUserName = WindowsIdentity.GetCurrent().Name;
        var currentShortName = currentUserName.Split('\\').Last();
        if (string.Equals(principalUserId, currentUserSid, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(principalUserId, currentUserName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(principalUserId, currentShortName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var accountName = new SecurityIdentifier(currentUserSid).Translate(typeof(NTAccount)).Value;
            var accountShortName = accountName.Split('\\').Last();
            return string.Equals(principalUserId, accountName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(principalUserId, accountShortName, StringComparison.OrdinalIgnoreCase);
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
    }

    private static bool TryParseBoundary(string? boundary, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(
            boundary,
            "yyyy-MM-dd'T'HH:mm:sszzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);

    private static string MultipleInstancesName(int value) => value switch
    {
        0 => "Parallel",
        1 => "Queue",
        2 => "IgnoreNew",
        3 => "StopExisting",
        _ => "Unknown"
    };

    private static SchedulerStatus ErrorStatus(string taskPath, string diagnosticCategory) =>
        new(taskPath, SchedulerState.ERROR, false, false, false, null, null, Array.Empty<string>(), diagnosticCategory);

    private static SchedulerOperationResult Failure(string diagnosticCategory, SchedulerStatus? status = null) =>
        new(false, diagnosticCategory, status);

    private static bool IsSchedulerException(Exception exception) =>
        exception is COMException or InvalidOperationException or UnauthorizedAccessException or
            System.Reflection.TargetInvocationException or System.Runtime.InteropServices.MarshalDirectiveException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
