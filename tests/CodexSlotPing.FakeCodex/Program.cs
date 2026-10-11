using System.Text.Json;

namespace CodexSlotPing.FakeCodex;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.SequenceEqual(["--version"], StringComparer.Ordinal))
        {
            Console.WriteLine("codex-cli 0.153.4");
            return 0;
        }

        if (args.SequenceEqual(["login", "status"], StringComparer.Ordinal))
        {
            Console.WriteLine("Logged in using ChatGPT");
            return 0;
        }

        if (args.SequenceEqual(["--help"], StringComparer.Ordinal))
        {
            Console.WriteLine("Codex CLI options: --model --sandbox --ask-for-approval --config");
            return 0;
        }

        if (args.Length == 2 && args[0] == "exec" && args[1] == "--help")
        {
            Console.WriteLine("exec [PROMPT]; use stdin prompt '-' for input from stdin.");
            Console.WriteLine("--ignore-user-config --skip-git-repo-check --ephemeral --json");
            return 0;
        }

        if (args.Length == 2 && args[0] == "--fixture" && args[1] == "success")
        {
            WriteSuccessFixture();
            return 0;
        }

        if (args.Contains("exec", StringComparer.Ordinal) && args.LastOrDefault() == "-")
        {
            RecordInvocationStart();
            if (int.TryParse(Environment.GetEnvironmentVariable("CODEXSLOTPING_FAKE_EXEC_DELAY_MILLISECONDS"), out var delayMilliseconds) &&
                delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds);
            }

            var prompt = await Console.In.ReadToEndAsync();
            if (prompt == "__FAKECODEX_DELAY__")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }

            if (prompt == "__FAKECODEX_NONZERO__")
            {
                Console.Error.WriteLine("fixture child failed");
                return 17;
            }

            if (prompt == "__FAKECODEX_QUOTA__")
            {
                WriteEvent(new { type = "error", error = new { message = "Usage limit reached in fixture." } });
                return 1;
            }

            if (prompt == "__FAKECODEX_NETWORK_UNKNOWN__")
            {
                WriteEvent(new { type = "error", message = "Temporary network transport failure in fixture." });
                return 1;
            }

            Console.Error.WriteLine("fixture skill/YAML warning");
            WriteSuccessFixture();
            return 0;
        }

        Console.Error.WriteLine("Expected a Codex exec stdin invocation or --fixture success.");
        return 2;
    }

    private static void WriteSuccessFixture()
    {
        WriteEvent(new { type = "thread.started", thread_id = "fixture-thread" });
        WriteEvent(new { type = "turn.started" });
        WriteEvent(new
        {
            type = "item.completed",
            item = new
            {
                id = "fixture-agent-message",
                type = "agent_message",
                text = "[redacted fixture response]"
            }
        });
        WriteEvent(new
        {
            type = "turn.completed",
            usage = new
            {
                input_tokens = 16650,
                cached_input_tokens = 8960,
                output_tokens = 12,
                reasoning_output_tokens = 0
            }
        });
    }

    private static void WriteEvent<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value));

    private static void RecordInvocationStart()
    {
        var logPath = Environment.GetEnvironmentVariable("CODEXSLOTPING_FAKE_LAUNCH_LOG_PATH");
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        var statePath = Environment.GetEnvironmentVariable("CODEXSLOTPING_FAKE_STATE_PATH");
        var claimedBeforeStart = false;
        if (!string.IsNullOrWhiteSpace(statePath) && File.Exists(statePath))
        {
            try
            {
                using var state = JsonDocument.Parse(File.ReadAllText(statePath));
                claimedBeforeStart = state.RootElement.GetProperty("formalSlots")
                    .EnumerateArray()
                    .Any(record => string.Equals(record.GetProperty("status").GetString(), "CLAIMED", StringComparison.Ordinal));
            }
            catch (JsonException)
            {
                claimedBeforeStart = false;
            }
        }

        var record = JsonSerializer.Serialize(new { claimedBeforeStart });
        File.AppendAllText(logPath, record + Environment.NewLine);
    }
}
