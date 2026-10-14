using System.Text.Json;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Logging;
using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Testing;
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
            using var pauseProtection = AutomationPauseProtection.CreateForCurrentSession();
            var timeZoneProvider = new WindowsTimeZoneProvider();
            var schedulerGateway = new WindowsTaskSchedulerGateway();
            var environmentDetector = new CodexEnvironmentDetector(new CodexExecutableResolver());
            var launcher = new CodexProcessLauncher(
                new CodexInvocationBuilder(),
                rawLogStore: new RawLogStore(paths.LogsDirectory, clock));
            var logWriter = new StructuredLogWriter(paths.LogsDirectory);
            var claimService = new FormalSlotClaimService(
                clock,
                timeZoneProvider,
                configStore,
                stateStore,
                mutex,
                new SlotAdmissionEvaluator(timeZoneProvider),
                new EligibilityEvaluator(),
                pauseProtection);

            if (options.Mode == RunnerMode.TestNow)
            {
                if (string.IsNullOrWhiteSpace(options.TestId))
                {
                    return (int)RunnerExitCode.InvalidArguments;
                }

                var testRunService = new TestRunService(
                    clock,
                    timeZoneProvider,
                    claimService,
                    environmentDetector,
                    launcher,
                    logWriter);
                var response = await testRunService.RunAsync(
                    options.TestId,
                    options.TestNowConfirmed,
                    CancellationToken.None).ConfigureAwait(false);
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    response,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                return response.Result?.Invocation.Classification switch
                {
                    CodexResultClassification.CONFIRMED_SUCCESS => (int)RunnerExitCode.ConfirmedSuccess,
                    CodexResultClassification.PRELAUNCH_FAILED => (int)RunnerExitCode.PrelaunchFailed,
                    CodexResultClassification.ATTEMPTED_UNCONFIRMED => (int)RunnerExitCode.AttemptedUnconfirmed,
                    null => (int)RunnerExitCode.NoDispatch,
                    _ => (int)RunnerExitCode.UnexpectedFailure
                };
            }

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
                logWriter,
                claimService);

            return (int)await orchestrator.RunFormalSlotAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return (int)RunnerExitCode.UnexpectedFailure;
        }
    }
}
