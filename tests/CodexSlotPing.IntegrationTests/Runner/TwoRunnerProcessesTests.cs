using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Runner;

namespace CodexSlotPing.IntegrationTests.Runner;

public sealed class TwoRunnerProcessesTests
{
    [Fact]
    public async Task TwoRunnerProcesses_OnlyOneClaimsAndLaunches()
    {
        var taskPath = $"\\CodexSlotPing-Test-{Guid.NewGuid():N}";
        var tempRoot = Path.Combine(Path.GetTempPath(), $"CodexSlotPing-TwoRunner-{Guid.NewGuid():N}");
        var localAppData = Path.Combine(tempRoot, "local-app-data");
        var dataPaths = new UserDataPaths(Path.Combine(localAppData, "CodexSlotPing"));
        var statePath = dataPaths.StatePath;
        var launchLogPath = Path.Combine(tempRoot, "fake-codex-launches.jsonl");
        var runnerPath = FindRunnerPath();
        var fakeBin = Path.Combine(tempRoot, "fake-bin");
        var gateway = new WindowsTaskSchedulerGateway();
        var taskDefinition = CreateTaskDefinition(taskPath, runnerPath);
        var processes = new List<Process>();

        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(fakeBin);
            CopyFakeCodexAsDirectCli(fakeBin);

            var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
            var firstSlot = TimeOnly.FromDateTime(nowLocal.DateTime);
            var slots = Enumerable.Range(0, 4)
                .Select(index => firstSlot.Add(TimeSpan.FromMinutes(30 * index)))
                .Order()
                .ToArray();
            var config = AppConfigDefaults.Create() with
            {
                TimeZone = "SYSTEM",
                Slots = slots.Select(time => time.ToString("HH:mm")).ToArray(),
                Prompt = "fixture-only cross-process race"
            };
            var atomicFile = new AtomicJsonFile(new PhysicalAtomicFileOperations());
            await new JsonConfigStore(atomicFile, dataPaths.ConfigPath).SaveAsync(config, CancellationToken.None);
            var stateStore = new JsonStateStore(atomicFile, statePath);
            await stateStore.SaveAsync(StateDocument.Empty() with
            {
                Eligibility = EligibilityState.Empty() with { ExternalManualUsageUnknown = false }
            }, CancellationToken.None);

            var installed = await gateway.InstallOrUpdateAsync(taskDefinition with { SlotTimes = slots }, CancellationToken.None);
            Assert.True(installed.Succeeded, installed.DiagnosticCategory);

            using var startGate = new ManualResetEventSlim(false);
            var firstStart = Task.Run(() =>
            {
                startGate.Wait();
                return StartRunner(runnerPath, taskPath, localAppData, fakeBin, launchLogPath, statePath);
            });
            var secondStart = Task.Run(() =>
            {
                startGate.Wait();
                return StartRunner(runnerPath, taskPath, localAppData, fakeBin, launchLogPath, statePath);
            });
            startGate.Set();
            processes.Add(await firstStart);
            processes.Add(await secondStart);
            var outputs = await Task.WhenAll(processes.Select(WaitForProcessAsync));

            Assert.Contains(RunnerExitCode.ConfirmedSuccess, outputs.Select(output => (RunnerExitCode)output.ExitCode));
            Assert.Equal(1, outputs.Count(output => output.ExitCode == (int)RunnerExitCode.NoDispatch));
            var launches = await File.ReadAllLinesAsync(launchLogPath);
            Assert.Single(launches);
            using var launch = JsonDocument.Parse(launches[0]);
            Assert.True(launch.RootElement.GetProperty("claimedBeforeStart").GetBoolean());

            Assert.True(File.Exists(statePath), $"Runner did not use isolated LocalAppData state path {statePath}.");
            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            Assert.Equal(StateDocument.CurrentSchemaVersion, state.RootElement.GetProperty("schemaVersion").GetInt32());
            var formalSlots = state.RootElement.GetProperty("formalSlots").EnumerateArray().ToArray();
            Assert.Single(formalSlots);
            Assert.Equal("CONFIRMED_SUCCESS", formalSlots[0].GetProperty("status").GetString());
            var eligibility = state.RootElement.GetProperty("eligibility");
            Assert.False(eligibility.GetProperty("hasUnresolvedClaim").GetBoolean());
            Assert.NotNull(eligibility.GetProperty("lastAttemptAtUtc").GetString());
        }
        finally
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
                process.Dispose();
            }
            try
            {
                await gateway.RemoveAsync(taskPath, CancellationToken.None);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }
    }

    private static SchedulerTaskDefinition CreateTaskDefinition(string taskPath, string runnerPath) =>
        new(taskPath, runnerPath, TimeZoneInfo.Local.Id,
            [new TimeOnly(7, 0), new TimeOnly(12, 1), new TimeOnly(17, 1), new TimeOnly(22, 1)],
            false, new DateOnly(2099, 1, 1));

    private static Process StartRunner(string runnerPath, string taskPath, string localAppData, string fakeBin,
        string launchLogPath, string statePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runnerPath,
            WorkingDirectory = Path.GetDirectoryName(runnerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--task-path");
        startInfo.ArgumentList.Add(taskPath);
        startInfo.Environment["LOCALAPPDATA"] = localAppData;
        startInfo.Environment["PATH"] = fakeBin + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        startInfo.Environment["CODEXSLOTPING_FAKE_LAUNCH_LOG_PATH"] = launchLogPath;
        startInfo.Environment["CODEXSLOTPING_FAKE_STATE_PATH"] = statePath;
        startInfo.Environment["CODEXSLOTPING_FAKE_EXEC_DELAY_MILLISECONDS"] = "750";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Runner process failed to start.");
    }

    private static async Task<ProcessOutput> WaitForProcessAsync(Process process)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return new ProcessOutput(process.ExitCode, await stdout, await stderr);
    }

    private static void CopyFakeCodexAsDirectCli(string destination)
    {
        var fakeCodex = GetFakeCodexPath();
        var sourceDirectory = Path.GetDirectoryName(fakeCodex)!;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory))
        {
            var name = Path.GetFileName(source);
            var targetName = string.Equals(name, Path.GetFileName(fakeCodex), StringComparison.OrdinalIgnoreCase)
                ? "codex.exe"
                : name;
            File.Copy(source, Path.Combine(destination, targetName), overwrite: true);
        }
    }

    private static string GetFakeCodexPath()
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = outputDirectory.Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the build configuration directory.");
        var repositoryRoot = outputDirectory;
        while (repositoryRoot is not null && !File.Exists(Path.Combine(repositoryRoot.FullName, "CodexSlotPing.sln")))
        {
            repositoryRoot = repositoryRoot.Parent;
        }

        if (repositoryRoot is null)
        {
            throw new InvalidOperationException("Could not locate the Codex Slot Ping repository root.");
        }

        return Path.Combine(repositoryRoot.FullName, "tests", "CodexSlotPing.FakeCodex", "bin", configuration,
            "net10.0", "CodexSlotPing.FakeCodex.exe");
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
            throw new DirectoryNotFoundException("Could not locate the solution root for the Runner process test.");
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the build configuration directory.");
        return Path.Combine(repository.FullName, "src", "CodexSlotPing.Runner", "bin", configuration,
            "net10.0-windows", "CodexSlotPing.Runner.exe");
    }

    private sealed record ProcessOutput(int ExitCode, string StandardOutput, string StandardError);
}
