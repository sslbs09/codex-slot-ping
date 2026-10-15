using System.Text.Json;
using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class UpdateTransactionJournalTests
{
    [Fact]
    public void Store_CreatesAndAtomicallyUpdatesOperationScopedJournal()
    {
        using var temp = new TemporaryDirectory();
        var (identity, paths, installRoot, extractRoot) = CreatePaths(temp.Path);
        var operationId = Guid.NewGuid();
        var journalStore = new UpdateTransactionJournalStore(paths);
        var prepared = CreateJournal(identity, operationId, installRoot, extractRoot, UpdateTransactionPhase.Prepared);

        Assert.Equal(UpdateTransactionJournalWriteStatus.Created, journalStore.TryCreate(prepared));
        Assert.Equal(UpdateTransactionJournalWriteStatus.AlreadyExists, journalStore.TryCreate(prepared));
        var updated = prepared with
        {
            Phase = UpdateTransactionPhase.BackupCreated,
            UpdatedUtc = prepared.UpdatedUtc.AddSeconds(1)
        };
        Assert.Equal(UpdateTransactionJournalWriteStatus.Updated, journalStore.TryUpdate(updated));

        var read = journalStore.Read(operationId);
        Assert.Equal(UpdateTransactionJournalReadStatus.Active, read.Status);
        Assert.Equal(updated, read.Journal);
    }

    [Fact]
    public void Store_RejectsWrongOwnerAndNonDeterministicSiblingPaths()
    {
        using var temp = new TemporaryDirectory();
        var (identity, paths, installRoot, extractRoot) = CreatePaths(temp.Path);
        var operationId = Guid.NewGuid();
        var store = new UpdateTransactionJournalStore(paths);
        var journal = CreateJournal(identity, operationId, installRoot, extractRoot, UpdateTransactionPhase.Prepared);
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created, store.TryCreate(journal));

        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            productId = "CodexSlotPing",
            installIdentity = new string('0', 64),
            operationId = operationId.ToString("D"),
            targetVersion = "0.3.1",
            installRoot,
            verifiedExtractRoot = extractRoot,
            newSiblingPath = journal.NewSiblingPath,
            backupSiblingPath = journal.BackupSiblingPath,
            phase = UpdateTransactionPhase.Prepared,
            createdUtc = journal.CreatedUtc,
            updatedUtc = journal.UpdatedUtc,
            launchedAppIdentity = (ProcessIdentity?)null
        });
        File.WriteAllText(store.GetJournalFilePath(operationId), json);

        Assert.Equal(UpdateTransactionJournalReadStatus.Invalid, store.Read(operationId).Status);
        var wrongPathJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            productId = "CodexSlotPing",
            installIdentity = identity.Key,
            operationId = operationId.ToString("D"),
            targetVersion = "0.3.1",
            installRoot,
            verifiedExtractRoot = extractRoot,
            newSiblingPath = Path.Combine(Path.GetDirectoryName(installRoot)!, "unexpected-new"),
            backupSiblingPath = journal.BackupSiblingPath,
            phase = UpdateTransactionPhase.Prepared,
            createdUtc = journal.CreatedUtc,
            updatedUtc = journal.UpdatedUtc,
            launchedAppIdentity = (ProcessIdentity?)null
        });
        File.WriteAllText(store.GetJournalFilePath(operationId), wrongPathJson);
        Assert.Equal(UpdateTransactionJournalReadStatus.Invalid, store.Read(operationId).Status);
    }

    [Fact]
    public void Store_RejectsMalformedAndUnknownSchemaFields()
    {
        using var temp = new TemporaryDirectory();
        var (identity, paths, installRoot, extractRoot) = CreatePaths(temp.Path);
        var operationId = Guid.NewGuid();
        var store = new UpdateTransactionJournalStore(paths);
        var journal = CreateJournal(identity, operationId, installRoot, extractRoot, UpdateTransactionPhase.Prepared);
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created, store.TryCreate(journal));
        File.WriteAllText(store.GetJournalFilePath(operationId), "{\"schemaVersion\":1,\"extra\":true}");

        Assert.Equal(UpdateTransactionJournalReadStatus.Invalid, store.Read(operationId).Status);
    }

    private static (InstallIdentity Identity, UpdateControlPaths Paths, string InstallRoot, string ExtractRoot) CreatePaths(string root)
    {
        var installRoot = Path.Combine(root, "Codex Slot Ping", "install");
        var extractRoot = Path.Combine(root, "staging", "extract");
        var identity = InstallIdentity.Create("CodexSlotPing", installRoot);
        return (identity, UpdateControlPaths.ForInstall(Path.Combine(root, "local-app-data"), identity), installRoot, extractRoot);
    }

    private static UpdateTransactionJournal CreateJournal(
        InstallIdentity identity,
        Guid operationId,
        string installRoot,
        string extractRoot,
        UpdateTransactionPhase phase)
    {
        var siblings = UpdateTransactionPaths.ForInstall(installRoot, operationId);
        var created = new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero);
        return new UpdateTransactionJournal(
            1, "CodexSlotPing", identity.Key, operationId, "0.3.1", installRoot, extractRoot,
            siblings.NewSiblingPath, siblings.BackupSiblingPath, phase, created, created, null);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexSlotPing.Update.Tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
