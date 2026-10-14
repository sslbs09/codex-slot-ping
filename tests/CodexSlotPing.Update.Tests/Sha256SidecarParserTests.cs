using System.Text;
using CodexSlotPing.App.Updates;

namespace CodexSlotPing.Update.Tests;

public sealed class Sha256SidecarParserTests
{
    private const string FileName = "CodexSlotPing-v0.3.0-win-x64.zip";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Parse_AcceptsLowercaseHash()
    {
        var result = Sha256SidecarParser.Parse(Encoding.ASCII.GetBytes($"{Hash}  {FileName}\n"), FileName);

        Assert.True(result.IsValid);
        Assert.Equal(Hash, result.Sha256);
    }

    [Fact]
    public void Parse_AcceptsUppercaseHash()
    {
        var result = Sha256SidecarParser.Parse(Encoding.ASCII.GetBytes($"{Hash.ToUpperInvariant()}  {FileName}\r\n"), FileName);

        Assert.True(result.IsValid);
        Assert.Equal(Hash.ToUpperInvariant(), result.Sha256);
    }

    [Theory]
    [InlineData("0123456789abcdef  " + FileName)]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  " + FileName)]
    [InlineData(Hash + "  wrong.zip")]
    [InlineData(Hash + "  " + FileName + "\n" + Hash + "  " + FileName)]
    [InlineData("")]
    public void Parse_RejectsInvalidRecords(string text)
    {
        var result = Sha256SidecarParser.Parse(Encoding.ASCII.GetBytes(text), FileName);

        Assert.False(result.IsValid);
        Assert.Null(result.Sha256);
    }

    [Fact]
    public void Parse_RejectsOversizedSidecar()
    {
        var result = Sha256SidecarParser.Parse(new byte[Sha256SidecarParser.MaximumBytes + 1], FileName);

        Assert.False(result.IsValid);
        Assert.Contains("sidecar-too-large", result.Errors);
    }
}
