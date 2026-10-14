using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class ReleaseSelectionTests
{
    [Fact]
    public void SelectHighest_IgnoresDraftPrereleaseAndMalformedTags()
    {
        var releases = new[]
        {
            Release("v9.0.0", draft: true),
            Release("v8.0.0-rc.1", prerelease: true),
            Release("v8.5.0-rc.2"),
            Release("release-next"),
            Release("v0.10.0"),
            Release("v0.9.0")
        };

        var selected = StableReleaseSelector.SelectHighest(releases);

        Assert.Equal("0.10.0", selected!.Version.ToString());
    }

    [Fact]
    public void SelectHighest_ReturnsNullWhenNoStableReleaseExists()
    {
        var releases = new[] { Release("v1.0.0-rc.1", prerelease: true), Release("bad-tag") };

        Assert.Null(StableReleaseSelector.SelectHighest(releases));
    }

    private static GitHubRelease Release(string tag, bool draft = false, bool prerelease = false) =>
        new(tag, tag, "notes", new Uri("https://github.com/sslbs09/codex-slot-ping/releases/tag/" + tag),
            draft, prerelease, []);
}
