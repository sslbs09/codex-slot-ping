using System.Text;
using System.Text.RegularExpressions;

namespace CodexSlotPing.App.Updates;

public sealed record Sha256SidecarParseResult(bool IsValid, string? Sha256, IReadOnlyList<string> Errors);

public static partial class Sha256SidecarParser
{
    public const int MaximumBytes = 4096;

    [GeneratedRegex("^(?<hash>[0-9A-Fa-f]{64})[ \\t]+(?<filename>[^ \\t]+)[ \\t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RecordPattern();

    public static Sha256SidecarParseResult Parse(ReadOnlySpan<byte> utf8Bytes, string expectedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFileName);
        if (utf8Bytes.Length > MaximumBytes)
        {
            return Invalid("sidecar-too-large");
        }
        if (utf8Bytes.IsEmpty)
        {
            return Invalid("sidecar-empty");
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(utf8Bytes);
        }
        catch (DecoderFallbackException)
        {
            return Invalid("sidecar-encoding-invalid");
        }

        var records = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (records.Length != 1)
        {
            return Invalid(records.Length == 0 ? "sidecar-empty" : "sidecar-record-count-invalid");
        }

        var match = RecordPattern().Match(records[0]);
        if (!match.Success)
        {
            return Invalid("sidecar-record-invalid");
        }
        if (!string.Equals(match.Groups["filename"].Value, expectedFileName, StringComparison.Ordinal))
        {
            return Invalid("sidecar-filename-mismatch");
        }

        return new Sha256SidecarParseResult(true, match.Groups["hash"].Value, []);
    }

    private static Sha256SidecarParseResult Invalid(string error) => new(false, null, [error]);
}
