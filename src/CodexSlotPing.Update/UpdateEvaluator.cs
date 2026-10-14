namespace CodexSlotPing.Update;

public static class UpdateEvaluator
{
    public static UpdateEvaluation Evaluate(string currentVersion, IReadOnlyList<GitHubRelease> releases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        ArgumentNullException.ThrowIfNull(releases);

        if (!SemVersion.TryParse(currentVersion, out var current))
        {
            return Failure(currentVersion, UpdateErrorCategory.InvalidCurrentVersion);
        }

        var target = StableReleaseSelector.SelectHighest(releases);
        if (target is null)
        {
            return Failure(currentVersion, UpdateErrorCategory.NoStableRelease);
        }

        var contract = ReleaseAssetContract.Evaluate(target.Release, target.Version);
        if (!contract.IsValid)
        {
            return new UpdateEvaluation(
                UpdateEvaluationStatus.Failed,
                currentVersion,
                target.Version.ToString(),
                target.Release.Name,
                target.Release.Body,
                target.Release.HtmlUrl,
                null,
                null,
                UpdateErrorCategory.InvalidAssetContract);
        }

        var status = target.Version.CompareTo(current) > 0
            ? UpdateEvaluationStatus.UpdateAvailable
            : UpdateEvaluationStatus.UpToDate;
        return new UpdateEvaluation(
            status,
            currentVersion,
            target.Version.ToString(),
            string.IsNullOrWhiteSpace(target.Release.Name) ? target.Release.TagName : target.Release.Name,
            target.Release.Body,
            target.Release.HtmlUrl,
            contract.Zip,
            contract.Checksum,
            null);
    }

    public static UpdateEvaluation Failure(string currentVersion, UpdateErrorCategory errorCategory) =>
        new(UpdateEvaluationStatus.Failed, currentVersion, null, null, null, null, null, null, errorCategory);
}
