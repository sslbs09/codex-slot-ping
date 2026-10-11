using System.Diagnostics;
using System.Text.Json;

namespace CodexSlotPing.IntegrationTests;

public sealed class FakeCodexFixtureTests
{
    [Fact]
    public async Task SuccessFixture_EmitsObservedJsonlShapeAndUsage()
    {
        var fakeCodexPath = GetFakeCodexPath();
        Assert.True(File.Exists(fakeCodexPath), $"FakeCodex executable was not found at {fakeCodexPath}.");

        var startInfo = new ProcessStartInfo
        {
            FileName = fakeCodexPath,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--fixture");
        startInfo.ArgumentList.Add("success");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(stderr);

        var parsedEvents = new List<JsonElement>();
        var invalidLines = new List<string>();
        foreach (var line in stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                parsedEvents.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
                invalidLines.Add(line);
            }
        }

        Assert.Empty(invalidLines);
        var events = parsedEvents.ToArray();

        Assert.Equal(["thread.started", "turn.started", "item.completed", "turn.completed"],
            events.Select(item => item.GetProperty("type").GetString() ?? string.Empty).ToArray());
        Assert.Equal("agent_message", events[2].GetProperty("item").GetProperty("type").GetString());
        Assert.Equal("[redacted fixture response]", events[2].GetProperty("item").GetProperty("text").GetString());

        var usage = events[3].GetProperty("usage");
        Assert.Equal(16650, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(8960, usage.GetProperty("cached_input_tokens").GetInt32());
        Assert.Equal(12, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(0, usage.GetProperty("reasoning_output_tokens").GetInt32());
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
}
