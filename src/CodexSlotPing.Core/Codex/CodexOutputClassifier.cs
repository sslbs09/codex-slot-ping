using System.Text.Json;

namespace CodexSlotPing.Core.Codex;

public sealed class CodexOutputClassifier
{
    public CodexInvocationResult Classify(
        string stdoutJsonl,
        string stderr,
        int? exitCode,
        bool timedOut,
        bool processStarted)
    {
        ArgumentNullException.ThrowIfNull(stdoutJsonl);
        ArgumentNullException.ThrowIfNull(stderr);

        if (!processStarted)
        {
            return Result(CodexResultClassification.PRELAUNCH_FAILED, false, exitCode, false, null, "process-start-failed", stderr);
        }

        if (timedOut)
        {
            return Result(CodexResultClassification.ATTEMPTED_UNCONFIRMED, true, exitCode, true, null, "process-timeout", stderr);
        }

        if (string.IsNullOrEmpty(stdoutJsonl))
        {
            return Result(CodexResultClassification.ATTEMPTED_UNCONFIRMED, true, exitCode, false, null,
                exitCode == 0 ? "completion-unconfirmed" : "nonzero-exit", stderr);
        }

        if (!stdoutJsonl.EndsWith('\n'))
        {
            return Result(CodexResultClassification.ATTEMPTED_UNCONFIRMED, true, exitCode, false, null, "truncated-jsonl", stderr);
        }

        var sawTurnCompleted = false;
        var sawCompletedAgentMessage = false;
        var failureCategory = (string?)null;
        CodexTokenUsage? usage = null;
        var lines = stdoutJsonl.Split('\n');
        for (var index = 0; index < lines.Length - 1; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                return Result(CodexResultClassification.ATTEMPTED_UNCONFIRMED, true, exitCode, false, usage, "malformed-jsonl", stderr);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                return Result(CodexResultClassification.ATTEMPTED_UNCONFIRMED, true, exitCode, false, usage, "malformed-jsonl", stderr);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String)
                {
                    return Result(CodexResultClassification.ATTEMPTED_UNCONFIRMED, true, exitCode, false, usage, "malformed-jsonl", stderr);
                }

                var eventType = typeElement.GetString()!;
                switch (eventType)
                {
                    case "turn.completed":
                        if (sawTurnCompleted)
                        {
                            failureCategory = PreferFailure(failureCategory, "multiple-terminal-events");
                        }
                        sawTurnCompleted = true;
                        usage = ReadUsage(root);
                        break;
                    case "item.completed":
                        if (IsCompletedAgentMessage(root))
                        {
                            sawCompletedAgentMessage = true;
                        }
                        break;
                    case "turn.failed":
                        failureCategory = PreferFailure(failureCategory, IsUsageLimit(root) ? "usage-limit" : "turn-failed");
                        break;
                    case "error":
                        failureCategory = PreferFailure(failureCategory, IsUsageLimit(root) ? "usage-limit" : "stream-error");
                        break;
                    default:
                        if (IsUnknownTerminalEvent(eventType))
                        {
                            failureCategory = PreferFailure(failureCategory, "unknown-terminal-event");
                        }
                        break;
                }
            }
        }

        if (exitCode != 0)
        {
            failureCategory = PreferFailure(failureCategory, "nonzero-exit");
        }

        if (failureCategory is not null)
        {
            return Result(CodexResultClassification.ATTEMPTED_UNCONFIRMED, true, exitCode, false, usage, failureCategory, stderr);
        }

        if (!sawTurnCompleted)
        {
            return Result(CodexResultClassification.ATTEMPTED_UNCONFIRMED, true, exitCode, false, usage, "completion-unconfirmed", stderr);
        }

        if (!sawCompletedAgentMessage)
        {
            return Result(CodexResultClassification.ATTEMPTED_UNCONFIRMED, true, exitCode, false, usage, "agent-message-missing", stderr);
        }

        return Result(CodexResultClassification.CONFIRMED_SUCCESS, true, exitCode, false, usage, null, stderr);
    }

    private static CodexInvocationResult Result(
        CodexResultClassification classification,
        bool processStarted,
        int? exitCode,
        bool timedOut,
        CodexTokenUsage? usage,
        string? diagnosticCategory,
        string stderr) =>
        new(
            classification,
            processStarted,
            exitCode,
            0,
            timedOut,
            usage,
            string.IsNullOrWhiteSpace(stderr) ? Array.Empty<string>() : ["stderr-output"],
            null,
            diagnosticCategory);

    private static bool IsCompletedAgentMessage(JsonElement root) =>
        root.TryGetProperty("item", out var item) &&
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty("type", out var itemType) &&
        itemType.ValueKind == JsonValueKind.String &&
        string.Equals(itemType.GetString(), "agent_message", StringComparison.Ordinal) &&
        item.TryGetProperty("text", out var text) &&
        text.ValueKind == JsonValueKind.String;

    private static CodexTokenUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parsed = new CodexTokenUsage(
            ReadNonNegativeInteger(usage, "input_tokens"),
            ReadNonNegativeInteger(usage, "cached_input_tokens"),
            ReadNonNegativeInteger(usage, "cache_write_input_tokens"),
            ReadNonNegativeInteger(usage, "output_tokens"),
            ReadNonNegativeInteger(usage, "reasoning_output_tokens"));

        return parsed.InputTokens is null && parsed.CachedInputTokens is null && parsed.CacheWriteInputTokens is null &&
               parsed.OutputTokens is null && parsed.ReasoningTokens is null
            ? null
            : parsed;
    }

    private static long? ReadNonNegativeInteger(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number) &&
        number >= 0
            ? number
            : null;

    private static bool IsUsageLimit(JsonElement root)
    {
        var message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                      error.TryGetProperty("message", out var nestedMessage) && nestedMessage.ValueKind == JsonValueKind.String
            ? nestedMessage.GetString()
            : root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;

        return message is not null &&
               (message.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("quota", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnknownTerminalEvent(string eventType) =>
        eventType.EndsWith(".failed", StringComparison.OrdinalIgnoreCase) ||
        eventType.EndsWith(".error", StringComparison.OrdinalIgnoreCase) ||
        eventType.EndsWith(".cancelled", StringComparison.OrdinalIgnoreCase);

    private static string PreferFailure(string? current, string candidate) =>
        current == "usage-limit" || candidate != "usage-limit"
            ? current ?? candidate
            : candidate;
}
