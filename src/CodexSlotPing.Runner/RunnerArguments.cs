namespace CodexSlotPing.Runner;

public sealed record RunnerOptions(string TaskPath);

public static class RunnerArguments
{
    public static RunnerOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 2 || !string.Equals(args[0], "--task-path", StringComparison.Ordinal))
        {
            throw new ArgumentException("Runner requires exactly one --task-path value.", nameof(args));
        }

        var taskPath = args[1];
        if (!IsValidTaskPath(taskPath))
        {
            throw new ArgumentException("The task path is invalid.", nameof(args));
        }

        return new RunnerOptions(taskPath);
    }

    private static bool IsValidTaskPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('\\') || value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }

        var taskName = value[1..];
        return taskName.Length > 0 &&
               string.Equals(taskName, taskName.Trim(), StringComparison.Ordinal) &&
               !taskName.Contains('\\') &&
               !taskName.Contains('/') &&
               taskName.All(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.');
    }
}

public enum RunnerExitCode
{
    ConfirmedSuccess = 0,
    NoDispatch = 10,
    PrelaunchFailed = 20,
    AttemptedUnconfirmed = 30,
    InvalidArguments = 64,
    UnexpectedFailure = 70
}
