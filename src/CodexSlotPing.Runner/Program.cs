using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Logging;
using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Runner;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        RunnerOptions options;
        try
        {
            options = RunnerArguments.Parse(args);
        }
        catch (ArgumentException)
        {
            return (int)RunnerExitCode.InvalidArguments;
        }

        if (!OperatingSystem.IsWindows())
        {
            return (int)RunnerExitCode.UnexpectedFailure;
        }

        var runnerPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(runnerPath))
        {
            return (int)RunnerExitCode.UnexpectedFailure;
        }

        try
        {
            var paths = UserDataPaths.ForCurrentUser();
            var clock = new SystemClock();
            var atomicFile = new AtomicJsonFile(new PhysicalAtomicFileOperations());
            var configStore = new JsonConfigStore(atomicFile, paths.ConfigPath);
            var stateStore = new JsonStateStore(atomicFile, paths.StatePath);
            using var mutex = new NamedStateMutex();
            var timeZoneProvider = new WindowsTimeZoneProvider();
            var schedulerGateway = new WindowsTaskSchedulerGateway();
            var environmentDetector = new CodexEnvironmentDetector(new CodexExecutableResolver());
            var launcher = new CodexProcessLauncher(
                new CodexInvocationBuilder(),
                rawLogStore: new RawLogStore(paths.LogsDirectory, clock));
            var logWriter = new StructuredLogWriter(paths.LogsDirectory);
            var orchestrator = new RunnerOrchestrator(
                options,
                runnerPath,
                clock,
                configStore,
                stateStore,
                mutex,
                timeZoneProvider,
                schedulerGateway,
                environmentDetector,
                launcher,
                logWriter);

            return (int)await orchestrator.RunFormalSlotAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return (int)RunnerExitCode.UnexpectedFailure;
        }
    }
}