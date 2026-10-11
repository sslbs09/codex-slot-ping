using CodexSlotPing.Core.Persistence;
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
        EnsureSupportedAndValid(state);
        return _jsonFile.WriteAsync(_path, state, cancellationToken);
    }

    private static void EnsureSupportedAndValid(StateDocument state)
    {
        if (state.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported state schema version: {state.SchemaVersion}.");
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
    }
}
