using System.Diagnostics;
using System.Globalization;

namespace CodexSlotPing.Update;

public sealed record UpdaterInvocation(
    Guid OperationId,
    SemVersion TargetVersion,
    string InstallRoot,
    string VerifiedExtractRoot,
    ProcessIdentity AppIdentity)
{
    public IReadOnlyList<string> ToArgumentList()
    {
        Validate();
        return
        [
            "--operation-id", OperationId.ToString("D"),
            "--target-version", TargetVersion.ToString(),
            "--install-root", Path.GetFullPath(InstallRoot),
            "--verified-extract-root", Path.GetFullPath(VerifiedExtractRoot),
            "--app-pid", AppIdentity.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--app-started-utc", AppIdentity.StartedUtc.ToString("O", CultureInfo.InvariantCulture)
        ];
    }

    public void AppendArgumentsTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        foreach (var argument in ToArgumentList())
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private void Validate()
    {
        if (OperationId == Guid.Empty || TargetVersion is null || !TargetVersion.IsStable ||
            !IsAbsolutePath(InstallRoot) || !IsAbsolutePath(VerifiedExtractRoot) ||
            AppIdentity is null || AppIdentity.ProcessId <= 0 || AppIdentity.StartedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The updater invocation is invalid.");
        }
    }

    private static bool IsAbsolutePath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value);
}

public static class UpdaterInvocationParser
{
    private static readonly HashSet<string> RequiredArguments = new(
        ["--operation-id", "--target-version", "--install-root", "--verified-extract-root", "--app-pid", "--app-started-utc"],
        StringComparer.Ordinal);

    public static bool TryParse(IReadOnlyList<string> arguments, out UpdaterInvocation? invocation)
    {
        invocation = null;
        if (arguments is null || arguments.Count != RequiredArguments.Count * 2)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var name = arguments[index];
            var value = arguments[index + 1];
            if (!RequiredArguments.Contains(name) || string.IsNullOrWhiteSpace(value) || !values.TryAdd(name, value))
            {
                return false;
            }
        }

        if (values.Count != RequiredArguments.Count ||
            !Guid.TryParseExact(values["--operation-id"], "D", out var operationId) || operationId == Guid.Empty ||
            !SemVersion.TryParse(values["--target-version"], out var targetVersion) || !targetVersion!.IsStable ||
            !int.TryParse(values["--app-pid"], NumberStyles.None, CultureInfo.InvariantCulture, out var appPid) || appPid <= 0 ||
            !DateTimeOffset.TryParseExact(values["--app-started-utc"], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var appStartedUtc) ||
            appStartedUtc.Offset != TimeSpan.Zero ||
            !Path.IsPathFullyQualified(values["--install-root"]) ||
            !Path.IsPathFullyQualified(values["--verified-extract-root"]))
        {
            return false;
        }

        try
        {
            invocation = new UpdaterInvocation(
                operationId,
                targetVersion,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(values["--install-root"])),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(values["--verified-extract-root"])),
                new ProcessIdentity(appPid, appStartedUtc));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
