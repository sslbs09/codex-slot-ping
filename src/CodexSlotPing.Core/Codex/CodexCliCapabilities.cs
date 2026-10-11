namespace CodexSlotPing.Core.Codex;

public sealed record CodexCliCapabilities(
    string Version,
    IReadOnlySet<string> SupportedOptions,
    IReadOnlySet<string> SupportedReasoningEfforts)
{
    public bool SupportsRequiredInvocationContract => RequiredOptionNames.All(SupportedOptions.Contains);

    public IReadOnlyList<string> MissingRequiredOptions =>
        RequiredOptionNames.Where(option => !SupportedOptions.Contains(option)).ToArray();

    public static IReadOnlySet<string> RequiredOptionNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "--model",
        "--sandbox",
        "--ask-for-approval",
        "--config",
        "--ignore-user-config",
        "--skip-git-repo-check",
        "--ephemeral",
        "--json",
        "stdin:-"
    };

    public static IReadOnlySet<string> DocumentedReasoningEfforts { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "low",
        "medium",
        "high",
        "xhigh",
        "max",
        "ultra"
    };
}

public sealed record EnvironmentStatus(
    bool DirectExecutableFound,
    string? ExecutablePath,
    string? CliVersion,
    bool? ChatGptLoginConfirmed,
    IReadOnlySet<string> SupportedReasoningEfforts,
    IReadOnlyList<string> DiagnosticCodes,
    CodexCliCapabilities? Capabilities)
{
    public bool IsReady =>
        DirectExecutableFound &&
        CliVersion is not null &&
        ChatGptLoginConfirmed == true &&
        Capabilities?.SupportsRequiredInvocationContract == true;
}
