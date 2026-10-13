using System.Text.Json;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;

namespace CodexSlotPing.Core.Tests.Persistence;

public sealed class AtomicJsonFileTests
{
    [Fact]
    public async Task Write_UsesUniqueTemporarySiblingsFlushesAndMovesOnFirstCreation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var files = new RecordingAtomicFileOperations();
        var json = new AtomicJsonFile(files);

        await json.WriteAsync(path, new Payload("first"), CancellationToken.None);
        var firstTemporaryPath = Assert.Single(files.CreatedPaths);
        Assert.Equal(directory.Path, Path.GetDirectoryName(firstTemporaryPath));
        Assert.EndsWith(".tmp", firstTemporaryPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "create", "flush", "move" }, files.Events.Select(entry => entry.Operation));

        files.ClearEvents();
        await json.WriteAsync(path, new Payload("second"), CancellationToken.None);

        var secondTemporaryPath = Assert.Single(files.CreatedPaths.Skip(1));
        Assert.NotEqual(firstTemporaryPath, secondTemporaryPath);
        Assert.Equal(new[] { "create", "flush", "replace" }, files.Events.Select(entry => entry.Operation));
        Assert.Equal("second", (await json.ReadAsync<Payload>(path, CancellationToken.None))!.Value);
    }

    [Fact]
    public async Task SerializationFailure_PreservesPriorBytesWithoutCreatingATemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var files = new RecordingAtomicFileOperations();
        var json = new AtomicJsonFile(files);
        await json.WriteAsync(path, new Payload("prior"), CancellationToken.None);
        var priorBytes = await File.ReadAllBytesAsync(path);
        files.ClearEvents();
        var cyclic = new CyclicPayload();
        cyclic.Next = cyclic;

        await Assert.ThrowsAsync<JsonException>(() => json.WriteAsync(path, cyclic, CancellationToken.None));

        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(files.Events);
        Assert.Empty(files.DeletedPaths);
    }

    [Fact]
    public async Task FlushFailure_PreservesPriorBytesAndDeletesOnlyItsTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var files = new RecordingAtomicFileOperations();
        var json = new AtomicJsonFile(files);
        await json.WriteAsync(path, new Payload("prior"), CancellationToken.None);
        var priorBytes = await File.ReadAllBytesAsync(path);
        var unrelatedTemporaryFile = Path.Combine(directory.Path, "unrelated.tmp");
        await File.WriteAllTextAsync(unrelatedTemporaryFile, "keep");
        files.FailOnFlush = true;
        files.ClearEvents();

        await Assert.ThrowsAsync<IOException>(() => json.WriteAsync(path, new Payload("replacement"), CancellationToken.None));

        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(path));
        Assert.True(File.Exists(unrelatedTemporaryFile));
        Assert.Single(files.DeletedPaths);
        Assert.NotEqual(unrelatedTemporaryFile, files.DeletedPaths[0]);
        Assert.False(File.Exists(files.DeletedPaths[0]));
    }

    [Fact]
    public async Task ReplaceFailure_PreservesPriorBytesAndDeletesItsTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var files = new RecordingAtomicFileOperations { FailOnReplace = true };
        var json = new AtomicJsonFile(files);
        await File.WriteAllTextAsync(path, "prior bytes");
        var priorBytes = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<IOException>(() => json.WriteAsync(path, new Payload("replacement"), CancellationToken.None));

        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(path));
        Assert.Single(files.DeletedPaths);
        Assert.False(File.Exists(files.DeletedPaths[0]));
    }

    [Fact]
    public async Task MissingConfigCreatesDefaultsButMalformedOrUnsupportedConfigIsPreserved()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        var json = new AtomicJsonFile(new RecordingAtomicFileOperations());
        var store = new JsonConfigStore(json, path);

        var created = await store.LoadOrCreateAsync(CancellationToken.None);

        var defaults = AppConfigDefaults.Create();
        Assert.Equal(defaults.SchemaVersion, created.SchemaVersion);
        Assert.Equal(defaults.LegacyFixedSlots, created.LegacyFixedSlots);
        Assert.Equal(defaults.TimeZone, created.TimeZone);
        Assert.Equal(defaults.Model, created.Model);
        Assert.Equal(defaults.ReasoningEffort, created.ReasoningEffort);
        Assert.Equal(defaults.Prompt, created.Prompt);
        Assert.True(File.Exists(path));
        var loadedDefaults = await store.LoadOrCreateAsync(CancellationToken.None);
        Assert.Equal(created.LegacyFixedSlots.ToArray(), loadedDefaults.LegacyFixedSlots.ToArray());
        Assert.Equal(created.Model, loadedDefaults.Model);

        const string malformed = "{\"schemaVersion\":1,";
        await File.WriteAllTextAsync(path, malformed);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadOrCreateAsync(CancellationToken.None));
        Assert.Equal(malformed, await File.ReadAllTextAsync(path));

        const string partial = "{\"schemaVersion\":1}";
        await File.WriteAllTextAsync(path, partial);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadOrCreateAsync(CancellationToken.None));
        Assert.Equal(partial, await File.ReadAllTextAsync(path));

        const string unsupported = "{\"schemaVersion\":3,\"enabled\":true,\"legacyFixedSlots\":[\"07:00\",\"12:01\",\"17:01\",\"22:01\"],\"timeZone\":\"SYSTEM\",\"model\":\"gpt-5.6-luna\",\"reasoningEffort\":\"low\",\"prompt\":\"Hi.\",\"wakeToRun\":false,\"strictSlotWindowMinutes\":3,\"captureRawLogs\":false}";
        await File.WriteAllTextAsync(path, unsupported);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadOrCreateAsync(CancellationToken.None));
        Assert.Equal(unsupported, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task V1Config_MigratesFixedSlotsAndPreservesOtherSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        var json = new AtomicJsonFile(new RecordingAtomicFileOperations());
        var store = new JsonConfigStore(json, path);
        const string legacy = "{\"schemaVersion\":1,\"enabled\":false,\"slots\":[\"07:00\",\"12:01\",\"17:01\",\"22:01\"],\"timeZone\":\"SYSTEM\",\"model\":\"gpt-5.6-sol\",\"reasoningEffort\":\"medium\",\"prompt\":\"Keep this\",\"wakeToRun\":true,\"strictSlotWindowMinutes\":3,\"captureRawLogs\":true}";
        await File.WriteAllTextAsync(path, legacy);

        var migrated = await store.LoadOrCreateAsync(CancellationToken.None);
        var persisted = await File.ReadAllTextAsync(path);

        Assert.Equal(2, migrated.SchemaVersion);
        Assert.Equal(new[] { "07:00", "12:01", "17:01", "22:01" }, migrated.LegacyFixedSlots);
        Assert.Null(migrated.Slots);
        Assert.False(migrated.Enabled);
        Assert.Equal("gpt-5.6-sol", migrated.Model);
        Assert.Equal("medium", migrated.ReasoningEffort);
        Assert.Equal("Keep this", migrated.Prompt);
        Assert.True(migrated.WakeToRun);
        Assert.True(migrated.CaptureRawLogs);
        Assert.Contains("\"legacyFixedSlots\"", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("\"slots\"", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialOrUnsupportedState_FailsClosedWithoutReplacingTheFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var json = new AtomicJsonFile(new RecordingAtomicFileOperations());
        var store = new JsonStateStore(json, path);

        var absent = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(StateDocument.CurrentSchemaVersion, absent.SchemaVersion);
        Assert.Empty(absent.FormalSlots);
        Assert.NotNull(absent.Eligibility);
        Assert.False(File.Exists(path));

        const string partial = "{\"schemaVersion\":1,";
        await File.WriteAllTextAsync(path, partial);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
        Assert.Equal(partial, await File.ReadAllTextAsync(path));

        const string missingOptionalField = "{\"schemaVersion\":1,\"highestObservedUtc\":null,\"formalSlots\":[]}";
        await File.WriteAllTextAsync(path, missingOptionalField);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
        Assert.Equal(missingOptionalField, await File.ReadAllTextAsync(path));

        const string unsupported = "{\"schemaVersion\":3,\"highestObservedUtc\":null,\"formalSlots\":[],\"lastTest\":null}";
        await File.WriteAllTextAsync(path, unsupported);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
        Assert.Equal(unsupported, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InterruptedV1MigrationWrite_PreservesLegacyBytesAndCanBeRetried()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var files = new RecordingAtomicFileOperations();
        var json = new AtomicJsonFile(files);
        var store = new JsonStateStore(json, path);
        var observedAt = new DateTimeOffset(2026, 1, 15, 7, 2, 0, TimeSpan.Zero);
        var legacy = new StateDocument(
            1,
            observedAt,
            [new FormalSlotRecord(
                new SlotId("Eastern Standard Time", new DateOnly(2026, 1, 15), new TimeOnly(7, 0)),
                1,
                FormalSlotStatus.ATTEMPTED_UNCONFIRMED,
                observedAt,
                "stream-error")],
            null);
        await json.WriteAsync(path, legacy, CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var migrated = StateDocumentMigrator.MigrateV1ToV2(legacy);
        files.FailOnReplace = true;

        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(migrated, CancellationToken.None));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, (await store.LoadAsync(CancellationToken.None)).SchemaVersion);

        files.FailOnReplace = false;
        await store.SaveAsync(migrated, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(StateDocument.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(observedAt, loaded.Eligibility!.LastAttemptAtUtc);
    }

    [Fact]
    public async Task AtomicReplaceFailure_PreservesPriorStateAndBlocksSuccessfulSave()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var files = new RecordingAtomicFileOperations();
        var json = new AtomicJsonFile(files);
        var store = new JsonStateStore(json, path);
        var original = StateDocument.Empty();
        await store.SaveAsync(original, CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(path);
        files.FailOnReplace = true;
        var changedHighWater = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var changed = original with
        {
            HighestObservedUtc = changedHighWater,
            Eligibility = original.Eligibility! with { HighestObservedUtc = changedHighWater }
        };

        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(changed, CancellationToken.None));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(original.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(original.HighestObservedUtc, loaded.HighestObservedUtc);
        Assert.Empty(loaded.FormalSlots);
        Assert.Null(loaded.LastTest);
    }

    [Fact]
    public async Task StateStore_RoundTripsFormalSlotTestMetadataAndActiveTestNowClaimWithoutPromptFields()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var store = new JsonStateStore(new AtomicJsonFile(new RecordingAtomicFileOperations()), path);
        var completedAtUtc = new DateTimeOffset(2026, 1, 15, 12, 2, 3, TimeSpan.Zero);
        var state = StateDocument.Empty() with
        {
            HighestObservedUtc = completedAtUtc,
            FormalSlots = [new FormalSlotRecord(
                new SlotId("Eastern Standard Time", new DateOnly(2026, 1, 15), new TimeOnly(12, 1)),
                2,
                FormalSlotStatus.CONFIRMED_SUCCESS,
                completedAtUtc,
                null)],
            LastTest = new TestRunRecord("prior-test-id", completedAtUtc, "gpt-5.6-luna", "low", "CONFIRMED_SUCCESS", 1234, 0),
            ActiveTestNowClaim = new TestNowClaimRecord("active-test-id", completedAtUtc),
            Eligibility = EligibilityState.Empty() with
            {
                HighestObservedUtc = completedAtUtc,
                ExternalManualUsageUnknown = false,
                LastAttemptAtUtc = completedAtUtc,
                LastConfirmedSuccessAtUtc = completedAtUtc,
                HasUnresolvedClaim = true,
                Observations = [new EligibilityObservation(
                    EligibilityObservationKind.ConfirmedSuccess,
                    completedAtUtc,
                    ObservationSource.AppScheduled,
                    Confidence.High)]
            }
        };

        await store.SaveAsync(state, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(state.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(state.HighestObservedUtc, loaded.HighestObservedUtc);
        Assert.Equal(state.FormalSlots, loaded.FormalSlots);
        Assert.Equal(state.LastTest, loaded.LastTest);
        Assert.Equal(state.ActiveTestNowClaim, loaded.ActiveTestNowClaim);
        Assert.Equal(state.Eligibility!.SchemaVersion, loaded.Eligibility!.SchemaVersion);
        Assert.Equal(state.Eligibility.HighestObservedUtc, loaded.Eligibility.HighestObservedUtc);
        Assert.Equal(state.Eligibility.LastAttemptAtUtc, loaded.Eligibility.LastAttemptAtUtc);
        Assert.Equal(state.Eligibility.LastConfirmedSuccessAtUtc, loaded.Eligibility.LastConfirmedSuccessAtUtc);
        Assert.Equal(state.Eligibility.ExternalManualUsageUnknown, loaded.Eligibility.ExternalManualUsageUnknown);
        Assert.Equal(state.Eligibility.HasUnresolvedClaim, loaded.Eligibility.HasUnresolvedClaim);
        Assert.Equal(state.Eligibility.Observations, loaded.Eligibility.Observations);
    }

    private sealed record Payload(string Value);

    private sealed class CyclicPayload
    {
        public CyclicPayload? Next { get; set; }
    }

    private sealed class RecordingAtomicFileOperations : IAtomicFileOperations
    {
        public List<(string Operation, string Path)> Events { get; } = [];

        public List<string> CreatedPaths { get; } = [];

        public List<string> DeletedPaths { get; } = [];

        public bool FailOnFlush { get; set; }

        public bool FailOnReplace { get; set; }

        public bool FileExists(string path) => File.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        public Stream CreateNew(string path)
        {
            Events.Add(("create", path));
            CreatedPaths.Add(path);
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        public void FlushToDisk(Stream stream)
        {
            Events.Add(("flush", ((FileStream)stream).Name));
            if (FailOnFlush)
            {
                throw new IOException("Injected flush failure.");
            }

            ((FileStream)stream).Flush(flushToDisk: true);
        }

        public void Replace(string temporaryPath, string destinationPath)
        {
            Events.Add(("replace", temporaryPath));
            if (FailOnReplace)
            {
                throw new IOException("Injected replace failure.");
            }

            File.Replace(temporaryPath, destinationPath, null);
        }

        public void Move(string temporaryPath, string destinationPath)
        {
            Events.Add(("move", temporaryPath));
            File.Move(temporaryPath, destinationPath);
        }

        public void Delete(string path)
        {
            Events.Add(("delete", path));
            DeletedPaths.Add(path);
            File.Delete(path);
        }

        public void ClearEvents() => Events.Clear();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodexSlotPing-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
