using System.Text.Json;

namespace CodexSlotPing.FakeCodex;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fixture" && args[1] == "success")
        {
            WriteSuccessFixture();
            return 0;
        }

        if (args.Contains("exec", StringComparer.Ordinal) && args.LastOrDefault() == "-")
        {
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
                text = "REDACTED_RESPONSE"
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
}
