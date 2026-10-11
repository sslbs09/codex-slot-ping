using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexSlotPing.Core.Codex;

public sealed class CodexEnvironmentDetector : ICodexEnvironmentDetector
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly CodexExecutableResolver _executableResolver;
    private readonly TimeSpan _commandTimeout;

    public CodexEnvironmentDetector(CodexExecutableResolver executableResolver, TimeSpan? commandTimeout = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<EnvironmentStatus> DetectAsync(CancellationToken cancellationToken)
    {
        var resolution = _executableResolver.Resolve();
        if (!resolution.IsDirectlyLaunchable || resolution.AbsolutePath is null)
        {
            return new EnvironmentStatus(
                false,
                null,
                null,
                null,
                new HashSet<string>(StringComparer.Ordinal),
                [resolution.DiagnosticCode ?? "direct-executable-not-found"],
                null);
        }

        var diagnostics = new List<string>();
        var versionOutput = await RunReadOnlyCommandAsync(resolution.AbsolutePath, ["--version"], cancellationToken).ConfigureAwait(false);
        var versionText = versionOutput.StandardOutput + Environment.NewLine + versionOutput.StandardError;
        var versionMatch = Regex.Match(versionText, @"\bcodex-cli\s+([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var version = versionOutput.ExitCode == 0 && versionMatch.Success ? $"codex-cli {versionMatch.Groups[1].Value}" : null;
        if (version is null)
        {
            diagnostics.Add("cli-version-unconfirmed");
        }

        var loginOutput = await RunReadOnlyCommandAsync(resolution.AbsolutePath, ["login", "status"], cancellationToken).ConfigureAwait(false);
        var loginText = loginOutput.StandardOutput + Environment.NewLine + loginOutput.StandardError;
        bool? loginConfirmed = null;
        if (loginOutput.ExitCode == 0 && loginText.Contains("Logged in using ChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            loginConfirmed = true;
        }
        else if (loginOutput.ExitCode == 0 &&
                 (loginText.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
                  loginText.Contains("Logged in using API key", StringComparison.OrdinalIgnoreCase)))
        {
            loginConfirmed = false;
        }

        if (loginConfirmed is null)
        {
            diagnostics.Add("chatgpt-login-unconfirmed");
        }
        else if (loginConfirmed == false)
        {
            diagnostics.Add("chatgpt-login-not-confirmed");
        }

        var globalHelp = await RunReadOnlyCommandAsync(resolution.AbsolutePath, ["--help"], cancellationToken).ConfigureAwait(false);
        var execHelp = await RunReadOnlyCommandAsync(resolution.AbsolutePath, ["exec", "--help"], cancellationToken).ConfigureAwait(false);
        var supportedOptions = DetectSupportedOptions(globalHelp, execHelp);
        var capabilities = version is null
            ? null
            : new CodexCliCapabilities(version, supportedOptions, CodexCliCapabilities.DocumentedReasoningEfforts);

        if (capabilities is null || !capabilities.SupportsRequiredInvocationContract)
        {
            diagnostics.AddRange(capabilities?.MissingRequiredOptions.Select(option => $"unsupported-option:{option}") ?? ["cli-capabilities-unconfirmed"]);
        }

        return new EnvironmentStatus(
            true,
            resolution.AbsolutePath,
            version,
            loginConfirmed,
            capabilities?.SupportedReasoningEfforts ?? new HashSet<string>(StringComparer.Ordinal),
            diagnostics.Distinct(StringComparer.Ordinal).ToArray(),
            capabilities);
    }

    private static IReadOnlySet<string> DetectSupportedOptions(CommandResult globalHelp, CommandResult execHelp)
    {
        var supported = new HashSet<string>(StringComparer.Ordinal);
        var globalText = globalHelp.ExitCode == 0 ? globalHelp.StandardOutput + Environment.NewLine + globalHelp.StandardError : string.Empty;
        var execText = execHelp.ExitCode == 0 ? execHelp.StandardOutput + Environment.NewLine + execHelp.StandardError : string.Empty;

        foreach (var option in new[] { "--model", "--sandbox", "--ask-for-approval", "--config" })
        {
            if (globalText.Contains(option, StringComparison.Ordinal))
            {
                supported.Add(option);
            }
        }

        foreach (var option in new[] { "--ignore-user-config", "--skip-git-repo-check", "--ephemeral", "--json" })
        {
            if (execText.Contains(option, StringComparison.Ordinal))
            {
                supported.Add(option);
            }
        }

        if (execText.Contains("stdin", StringComparison.OrdinalIgnoreCase) &&
            execText.Contains("[PROMPT]", StringComparison.OrdinalIgnoreCase))
        {
            supported.Add("stdin:-");
        }

        return supported;
    }

    private async Task<CommandResult> RunReadOnlyCommandAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        CodexInvocationBuilder.RemoveCredentialOverrides(startInfo.Environment);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return CommandResult.Failed;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return CommandResult.Failed;
        }

        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_commandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return CommandResult.Failed;
        }

        var output = await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);
        return new CommandResult(process.ExitCode, output, error);
    }

    private static void KillProcessTree(Process process)
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
    }

    private sealed record CommandResult(int? ExitCode, string StandardOutput, string StandardError)
    {
        public static CommandResult Failed { get; } = new(null, string.Empty, string.Empty);
    }
}
