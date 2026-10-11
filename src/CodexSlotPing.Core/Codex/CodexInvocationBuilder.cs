using System.Diagnostics;
using System.Text;

namespace CodexSlotPing.Core.Codex;

public sealed class CodexInvocationBuilder
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly HashSet<string> CredentialOverrideNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPENAI_API_KEY",
        "CODEX_API_KEY",
        "CODEX_ACCESS_TOKEN",
        "OPENAI_BASE_URL",
        "CODEX_OSS_BASE_URL",
        "CODEX_APP_SERVER_CHATGPT_BASE_URL",
        "CODEX_AUTHAPI_BASE_URL",
        "CODEX_AGENT_IDENTITY_AUTHAPI_BASE_URL",
        "CODEX_AGENT_IDENTITY_JWKS_BASE_URL"
    };

    public ProcessStartInfo Build(
        CodexInvocationRequest request,
        IEnumerable<KeyValuePair<string, string?>>? environmentOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ExecutablePath) || !Path.IsPathRooted(request.ExecutablePath) ||
            !request.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Codex must be launched through an absolute native executable path.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.ReasoningEffort) ||
            string.IsNullOrWhiteSpace(request.Prompt) || string.IsNullOrWhiteSpace(request.InvocationId))
        {
            throw new ArgumentException("The invocation request is incomplete.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Path.IsPathRooted(request.WorkingDirectory))
        {
            throw new ArgumentException("The working directory must be an absolute app-owned path.", nameof(request));
        }

        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        if (!Directory.Exists(workingDirectory) || Directory.EnumerateFileSystemEntries(workingDirectory).Any())
        {
            throw new ArgumentException("Codex requires an existing empty app-owned working directory.", nameof(request));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(request.ExecutablePath),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        if (environmentOverrides is not null)
        {
            foreach (var (name, value) in environmentOverrides)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        RemoveCredentialOverrides(startInfo.Environment);

        foreach (var argument in new[]
        {
            "--ask-for-approval", "never",
            "--sandbox", "read-only",
            "--model", request.Model,
            "--config", $"model_reasoning_effort={request.ReasoningEffort}",
            "exec",
            "--ignore-user-config",
            "--skip-git-repo-check",
            "--ephemeral",
            "--json",
            "-"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static void RemoveCredentialOverrides(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var name in environment.Keys.Where(CredentialOverrideNames.Contains).ToArray())
        {
            environment.Remove(name);
        }
    }
}
