using System.Text.Json;
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
        Assert.Equal(defaults.Slots, created.Slots);
        Assert.Equal(defaults.TimeZone, created.TimeZone);
        Assert.Equal(defaults.Model, created.Model);
        Assert.Equal(defaults.ReasoningEffort, created.ReasoningEffort);
        Assert.Equal(defaults.Prompt, created.Prompt);
        Assert.True(File.Exists(path));
        var loadedDefaults = await store.LoadOrCreateAsync(CancellationToken.None);
        Assert.Equal(created.Slots.ToArray(), loadedDefaults.Slots.ToArray());
        Assert.Equal(created.Model, loadedDefaults.Model);

        const string malformed = "{\"schemaVersion\":1,";
        await File.WriteAllTextAsync(path, malformed);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadOrCreateAsync(CancellationToken.None));
        Assert.Equal(malformed, await File.ReadAllTextAsync(path));

        const string partial = "{\"schemaVersion\":1}";
        await File.WriteAllTextAsync(path, partial);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadOrCreateAsync(CancellationToken.None));
        Assert.Equal(partial, await File.ReadAllTextAsync(path));

        const string unsupported = "{\"schemaVersion\":2,\"enabled\":true,\"slots\":[\"07:00\",\"12:01\",\"17:01\",\"22:01\"],\"timeZone\":\"SYSTEM\",\"model\":\"gpt-5.6-luna\",\"reasoningEffort\":\"low\",\"prompt\":\"Hi.\",\"wakeToRun\":false,\"strictSlotWindowMinutes\":3,\"captureRawLogs\":false}";
        await File.WriteAllTextAsync(path, unsupported);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadOrCreateAsync(CancellationToken.None));
        Assert.Equal(unsupported, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PartialOrUnsupportedState_FailsClosedWithoutReplacingTheFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var json = new AtomicJsonFile(new RecordingAtomicFileOperations());
        var store = new JsonStateStore(json, path);

        var absent = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(1, absent.SchemaVersion);
        Assert.Empty(absent.FormalSlots);
        Assert.False(File.Exists(path));

        const string partial = "{\"schemaVersion\":1,";
        await File.WriteAllTextAsync(path, partial);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
        Assert.Equal(partial, await File.ReadAllTextAsync(path));

        const string missingOptionalField = "{\"schemaVersion\":1,\"highestObservedUtc\":null,\"formalSlots\":[]}";
        await File.WriteAllTextAsync(path, missingOptionalField);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
        Assert.Equal(missingOptionalField, await File.ReadAllTextAsync(path));

        const string unsupported = "{\"schemaVersion\":2,\"highestObservedUtc\":null,\"formalSlots\":[],\"lastTest\":null}";
        await File.WriteAllTextAsync(path, unsupported);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
        Assert.Equal(unsupported, await File.ReadAllTextAsync(path));
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
        var changed = original with { HighestObservedUtc = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero) };

        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(changed, CancellationToken.None));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(original.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(original.HighestObservedUtc, loaded.HighestObservedUtc);
        Assert.Empty(loaded.FormalSlots);
        Assert.Null(loaded.LastTest);
    }

    [Fact]
    public async Task StateStore_RoundTripsFormalSlotAndTestMetadataWithoutPromptFields()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var store = new JsonStateStore(new AtomicJsonFile(new RecordingAtomicFileOperations()), path);
        var completedAtUtc = new DateTimeOffset(2026, 1, 15, 12, 2, 3, TimeSpan.Zero);
        var state = new StateDocument(
            1,
            completedAtUtc,
            [new FormalSlotRecord(
                new SlotId("Eastern Standard Time", new DateOnly(2026, 1, 15), new TimeOnly(12, 1)),
                2,
                FormalSlotStatus.CLAIMED,
                completedAtUtc,
                null)],
            new TestRunRecord("test-id", completedAtUtc, "gpt-5.6-luna", "low", "CONFIRMED_SUCCESS", 1234, 0));

        await store.SaveAsync(state, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(state.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(state.HighestObservedUtc, loaded.HighestObservedUtc);
        var slot = Assert.Single(loaded.FormalSlots);
        Assert.Equal(state.FormalSlots[0], slot);
        Assert.Equal(state.LastTest, loaded.LastTest);
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
