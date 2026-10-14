using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class SemVersionTests
{
    [Fact]
    public void CompareTo_UsesNumericComponentsRatherThanText()
    {
        Assert.True(SemVersion.Parse("0.10.0").CompareTo(SemVersion.Parse("0.9.0")) > 0);
    }

    [Fact]
    public void CompareTo_ReturnsEqualForTheSamePrecedence()
    {
        Assert.Equal(0, SemVersion.Parse("1.2.3").CompareTo(SemVersion.Parse("1.2.3")));
    }

    [Fact]
    public void CompareTo_StableReleaseOutranksPrerelease()
    {
        Assert.True(SemVersion.Parse("1.0.0").CompareTo(SemVersion.Parse("1.0.0-rc.1")) > 0);
    }

    [Fact]
    public void CompareTo_IgnoresBuildMetadata()
    {
        Assert.Equal(0, SemVersion.Parse("1.2.3+build.1").CompareTo(SemVersion.Parse("1.2.3+build.9")));
    }

    [Fact]
    public void CompareTo_ComparesPrereleaseIdentifiersBySemVerRules()
    {
        Assert.True(SemVersion.Parse("1.0.0-alpha.10").CompareTo(SemVersion.Parse("1.0.0-alpha.2")) > 0);
        Assert.True(SemVersion.Parse("1.0.0-alpha.1").CompareTo(SemVersion.Parse("1.0.0-alpha")) > 0);
        Assert.True(SemVersion.Parse("1.0.0-1").CompareTo(SemVersion.Parse("1.0.0-alpha")) < 0);
    }

    [Theory]
    [InlineData("01.0.0")]
    [InlineData("1.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-")]
    public void TryParse_RejectsInvalidSemVer(string value)
    {
        Assert.False(SemVersion.TryParse(value, out _));
    }

    [Fact]
    public void TryParseTag_AllowsConventionalVPrefix()
    {
        Assert.True(SemVersion.TryParseTag("v1.2.3", out var parsed));
        Assert.Equal("1.2.3", parsed!.ToString());
    }
}
