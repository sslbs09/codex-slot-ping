using System.Diagnostics;
using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Runner;
using CodexSlotPing.Update;

namespace CodexSlotPing.IntegrationTests.Runner;

public sealed class RunnerUpdateIntentProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveIntentStopsScheduledAndTestNowProcessesBeforeStateOrCodex(bool testNow)
    {
        using var temp = new TemporaryDirectory();
        var installRoot = Path.Combine(temp.Path, "portable-runner");
        var localAppData = Path.Combine(temp.Path, "local-app-data");
        var fakeBin = Path.Combine(temp.Path, "fake-bin");
        var launchLogPath = Path.Combine(temp.Path, "fake-codex-launches.jsonl");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(fakeBin);

        var sourceRunner = FindRunnerPath();
        CopyDirectory(Path.GetDirectoryName(sourceRunner)!, installRoot);
        CopyFakeCodexAsDirectCli(fakeBin);

        var identity = InstallIdentity.Create("CodexSlotPing", installRoot);
        var controlPaths = UpdateControlPaths.ForInstall(localAppData, identity);
        var intent = new UpdateIntent(
            1,
            "CodexSlotPing",
            identity.Key,
            Guid.NewGuid(),
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        Assert.Equal(UpdateIntentCreateStatus.Created, new UpdateIntentStore(controlPaths).Create(intent));

        var dataPaths = new UserDataPaths(Path.Combine(localAppData, "CodexSlotPing"));
        var runnerPath = Path.Combine(installRoot, Path.GetFileName(sourceRunner));
        var startInfo = new ProcessStartInfo
        {
            FileName = runnerPath,
            WorkingDirectory = installRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (testNow)
        {
            startInfo.ArgumentList.Add("--test-now");
            startInfo.ArgumentList.Add(Guid.NewGuid().ToString("N"));
            startInfo.ArgumentList.Add("--confirmed");
        }
        else
        {
            startInfo.ArgumentList.Add("--task-path");
            startInfo.ArgumentList.Add("\\UpdateGate-Test");
        }
        startInfo.Environment["LOCALAPPDATA"] = localAppData;
        startInfo.Environment["PATH"] = fakeBin + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        startInfo.Environment["CODEXSLOTPING_FAKE_LAUNCH_LOG_PATH"] = launchLogPath;
        startInfo.Environment["CODEXSLOTPING_FAKE_STATE_PATH"] = dataPaths.StatePath;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Runner process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var output = new ProcessOutput(process.ExitCode, await stdout, await stderr);

        Assert.Equal((int)RunnerExitCode.UpdateInProgress, output.ExitCode);
        Assert.False(File.Exists(dataPaths.StatePath), "Runner created or changed StateDocument before the update gate.");
        Assert.False(File.Exists(launchLogPath), "FakeCodex was launched while an update intent was active.");
        Assert.Equal(UpdateIntentReadStatus.Active, new UpdateIntentStore(controlPaths).Read().Status);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void CopyFakeCodexAsDirectCli(string destination)
    {
        var fakeCodex = GetFakeCodexPath();
        var sourceDirectory = Path.GetDirectoryName(fakeCodex)!;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory))
        {
            var fileName = Path.GetFileName(source);
            var targetName = string.Equals(fileName, Path.GetFileName(fakeCodex), StringComparison.OrdinalIgnoreCase)
                ? "codex.exe"
                : fileName;
            File.Copy(source, Path.Combine(destination, targetName));
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
            throw new InvalidOperationException("Could not locate the repository root.");
        }

        return Path.Combine(repositoryRoot.FullName, "tests", "CodexSlotPing.FakeCodex", "bin", configuration,
            "net10.0", "CodexSlotPing.FakeCodex.exe");
    }

    private static string FindRunnerPath()
    {
        var repositoryRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repositoryRoot is not null && !File.Exists(Path.Combine(repositoryRoot.FullName, "CodexSlotPing.sln")))
        {
            repositoryRoot = repositoryRoot.Parent;
        }
        if (repositoryRoot is null)
        {
            throw new DirectoryNotFoundException("Could not locate the repository root.");
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the build configuration directory.");
        return Path.Combine(repositoryRoot.FullName, "src", "CodexSlotPing.Runner", "bin", configuration,
            "net10.0-windows", "CodexSlotPing.Runner.exe");
    }

    private sealed record ProcessOutput(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "CodexSlotPing.IntegrationTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
