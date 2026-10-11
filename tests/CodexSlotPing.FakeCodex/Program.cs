using System.Text.Json;

namespace CodexSlotPing.FakeCodex;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fixture" && args[1] == "success")
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
            return 0;
        }

        Console.Error.WriteLine("Expected --fixture success.");
        return 2;
    }

    private static void WriteEvent<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value));
}
