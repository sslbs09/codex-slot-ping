using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Slots;

namespace CodexSlotPing.Core.State;

public sealed class JsonStateStore : IStateStore
{
    private readonly AtomicJsonFile _jsonFile;
    private readonly string _path;

    public JsonStateStore(AtomicJsonFile jsonFile, string path)
    {
        _jsonFile = jsonFile ?? throw new ArgumentNullException(nameof(jsonFile));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<StateDocument> LoadAsync(CancellationToken cancellationToken)
    {
        var state = await _jsonFile.ReadAsync<StateDocument>(_path, cancellationToken).ConfigureAwait(false)
            ?? StateDocument.Empty();
        EnsureSupportedAndValid(state);
        return state;
    }

    public Task SaveAsync(StateDocument state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var stateToSave = state.SchemaVersion == 1
            ? StateDocumentMigrator.MigrateV1ToV2(state)
            : state;
        EnsureSupportedAndValid(stateToSave);
        return _jsonFile.WriteAsync(_path, stateToSave, cancellationToken);
    }

    private static void EnsureSupportedAndValid(StateDocument state)
    {
        if (state.SchemaVersion is not 1 and not StateDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported state schema version: {state.SchemaVersion}.");
        }

        if (state.SchemaVersion == 1 && state.Eligibility is not null)
        {
            throw new InvalidDataException("The v1 state document must not contain eligibility state.");
        }

        if (state.FormalSlots is null)
        {
            throw new InvalidDataException("The state document is missing formalSlots.");
        }

        var seenSlotIds = new HashSet<SlotId>();
        foreach (var record in state.FormalSlots)
        {
            if (record is null ||
                record.SlotId is null ||
                string.IsNullOrWhiteSpace(record.SlotId.WindowsTimeZoneId) ||
                record.Position is < 1 or > 4 ||
                !Enum.IsDefined(record.Status) ||
                record.Status == FormalSlotStatus.PENDING ||
                !seenSlotIds.Add(record.SlotId))
            {
                throw new InvalidDataException("The state document contains an invalid formal slot record.");
            }
        }

        if (state.LastTest is { } lastTest &&
            (string.IsNullOrWhiteSpace(lastTest.TestId) ||
             string.IsNullOrWhiteSpace(lastTest.Model) ||
             string.IsNullOrWhiteSpace(lastTest.ReasoningEffort) ||
             string.IsNullOrWhiteSpace(lastTest.ResultCategory) ||
             lastTest.ElapsedMilliseconds < 0))
        {
            throw new InvalidDataException("The state document contains an invalid Test Now record.");
        }

        if (state.ActiveTestNowClaim is { } activeTestNowClaim &&
            (string.IsNullOrWhiteSpace(activeTestNowClaim.TestId) ||
             activeTestNowClaim.ClaimedAtUtc.Offset != TimeSpan.Zero ||
             string.Equals(activeTestNowClaim.TestId, state.LastTest?.TestId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The state document contains an invalid active Test Now claim.");
        }

        if (state.SchemaVersion == StateDocument.CurrentSchemaVersion)
        {
            EnsureEligibilityIsValid(state);
        }
    }

    private static void EnsureEligibilityIsValid(StateDocument state)
    {
        var eligibility = state.Eligibility;
        if (eligibility is null)
        {
            throw new InvalidDataException("The state document is missing eligibility state.");
        }

        if (eligibility.SchemaVersion != EligibilityState.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported eligibility schema version: {eligibility.SchemaVersion}.");
        }

        if (eligibility.Observations is null ||
            eligibility.Observations.Any(observation =>
                observation is null ||
                !Enum.IsDefined(observation.Kind) ||
                !Enum.IsDefined(observation.Source) ||
                !Enum.IsDefined(observation.Confidence)))
        {
            throw new InvalidDataException("The state document contains an invalid eligibility observation.");
        }

        if (state.HighestObservedUtc != eligibility.HighestObservedUtc)
        {
            throw new InvalidDataException("The state document contains inconsistent highestObservedUtc values.");
        }

        var hasUnresolvedClaim = state.FormalSlots.Any(record => record.Status == FormalSlotStatus.CLAIMED) ||
                                 state.ActiveTestNowClaim is not null;
        if (eligibility.HasUnresolvedClaim != hasUnresolvedClaim)
        {
            throw new InvalidDataException("The state document contains an inconsistent unresolved claim marker.");
        }
    }
}
