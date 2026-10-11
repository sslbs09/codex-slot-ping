using System.IO;

namespace CodexSlotPing.App;

public static class RunnerPathResolver
{
    public const string RunnerFileName = "CodexSlotPing.Runner.exe";

    public static string Resolve(string appBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);
        return Path.GetFullPath(Path.Combine(appBaseDirectory, "Runner", RunnerFileName));
    }
}
