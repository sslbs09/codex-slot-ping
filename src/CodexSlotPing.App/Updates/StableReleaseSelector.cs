using CodexSlotPing.Update;

namespace CodexSlotPing.App.Updates;

public static class StableReleaseSelector
{
    public static StableRelease? SelectHighest(IEnumerable<GitHubRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        StableRelease? highest = null;

        foreach (var release in releases)
        {
            if (release.Draft || release.Prerelease || !SemVersion.TryParseTag(release.TagName, out var version) || !version!.IsStable)
            {
                continue;
            }

            if (highest is null || version.CompareTo(highest.Version) > 0)
            {
                highest = new StableRelease(release, version);
            }
        }

        return highest;
    }
}
