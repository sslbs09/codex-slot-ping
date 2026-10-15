using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSlotPing.Update;

public enum UpdateTransactionPhase
{
    Prepared,
    BackupCreated,
    ReplacementInstalled,
    NewAppLaunched,
    Completed,
    RollbackStarted,
    RolledBack,
    RecoveryRequired
}

public sealed record UpdateTransactionJournal(
    int SchemaVersion,
    string ProductId,
    string InstallIdentity,
    Guid OperationId,
    string TargetVersion,
    string InstallRoot,
    string VerifiedExtractRoot,
    string NewSiblingPath,
    string BackupSiblingPath,
    UpdateTransactionPhase Phase,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    ProcessIdentity? LaunchedAppIdentity);

public sealed record UpdateTransactionPathSet(
    string InstallRoot,
    string ParentDirectory,
    string NewSiblingPath,
    string BackupSiblingPath,
    string FailedSiblingPath);

public static class UpdateTransactionPaths
{
    public static UpdateTransactionPathSet ForInstall(string installRoot, Guid operationId)
    {
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(installRoot) || !Path.IsPathFullyQualified(installRoot))
        {
            throw new ArgumentException("An absolute install root and non-empty operation ID are required.");
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var parent = Path.GetDirectoryName(normalizedRoot)
            ?? throw new ArgumentException("The install root must have a parent directory.", nameof(installRoot));
        var operationKey = operationId.ToString("N");
        var prefix = $".CodexSlotPing-update-{operationKey}";
        return new UpdateTransactionPathSet(
            normalizedRoot,
            parent,
            Path.Combine(parent, prefix + "-new"),
            Path.Combine(parent, prefix + "-backup"),
            Path.Combine(parent, prefix + "-failed"));
    }
}

public enum UpdateTransactionJournalReadStatus
{
    Missing,
    Active,
    Invalid,
    IoError
}

public sealed record UpdateTransactionJournalReadResult(
    UpdateTransactionJournalReadStatus Status,
    UpdateTransactionJournal? Journal,
    string? Error)
{
    public static UpdateTransactionJournalReadResult Missing() => new(UpdateTransactionJournalReadStatus.Missing, null, null);
    public static UpdateTransactionJournalReadResult Active(UpdateTransactionJournal journal) => new(UpdateTransactionJournalReadStatus.Active, journal, null);
    public static UpdateTransactionJournalReadResult Invalid(string error) => new(UpdateTransactionJournalReadStatus.Invalid, null, error);
    public static UpdateTransactionJournalReadResult IoError(string error) => new(UpdateTransactionJournalReadStatus.IoError, null, error);
}

public enum UpdateTransactionJournalWriteStatus
{
    Created,
    Updated,
    AlreadyExists,
    Invalid,
    StorageFailure
}

public interface IUpdateTransactionJournalStore
{
    string GetJournalFilePath(Guid operationId);
    UpdateTransactionJournalReadResult Read(Guid operationId);
    UpdateTransactionJournalWriteStatus TryCreate(UpdateTransactionJournal journal);
    UpdateTransactionJournalWriteStatus TryUpdate(UpdateTransactionJournal journal);
}

public sealed class UpdateTransactionJournalStore(UpdateControlPaths paths) : IUpdateTransactionJournalStore
{
    private static readonly HashSet<string> Properties = new(
        ["schemaVersion", "productId", "installIdentity", "operationId", "targetVersion", "installRoot", "verifiedExtractRoot", "newSiblingPath", "backupSiblingPath", "phase", "createdUtc", "updatedUtc", "launchedAppIdentity"],
        StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public string GetJournalFilePath(Guid operationId) => paths.JournalFilePath(operationId);

    public UpdateTransactionJournalReadResult Read(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            return UpdateTransactionJournalReadResult.Invalid("operation-id-invalid");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(GetJournalFilePath(operationId)));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return UpdateTransactionJournalReadResult.Invalid("journal-root-invalid");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!Properties.Contains(property.Name) || !seen.Add(property.Name))
                {
                    return UpdateTransactionJournalReadResult.Invalid("journal-properties-invalid");
                }
            }
            if (seen.Count != Properties.Count)
            {
                return UpdateTransactionJournalReadResult.Invalid("journal-properties-incomplete");
            }

            var journal = JsonSerializer.Deserialize<UpdateTransactionJournal>(root.GetRawText(), JsonOptions);
            return journal is not null && IsValid(journal, operationId)
                ? UpdateTransactionJournalReadResult.Active(journal)
                : UpdateTransactionJournalReadResult.Invalid("journal-contract-invalid");
        }
        catch (FileNotFoundException)
        {
            return UpdateTransactionJournalReadResult.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return UpdateTransactionJournalReadResult.Missing();
        }
        catch (JsonException)
        {
            return UpdateTransactionJournalReadResult.Invalid("journal-json-invalid");
        }
        catch (UnauthorizedAccessException)
        {
            return UpdateTransactionJournalReadResult.IoError("journal-access-denied");
        }
        catch (IOException)
        {
            return UpdateTransactionJournalReadResult.IoError("journal-read-failed");
        }
        catch (ArgumentException)
        {
            return UpdateTransactionJournalReadResult.Invalid("journal-path-invalid");
        }
    }

    public UpdateTransactionJournalWriteStatus TryCreate(UpdateTransactionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (!IsValid(journal, journal.OperationId))
        {
            return UpdateTransactionJournalWriteStatus.Invalid;
        }
        return Write(journal, replace: false);
    }

    public UpdateTransactionJournalWriteStatus TryUpdate(UpdateTransactionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var current = Read(journal.OperationId);
        if (current.Status != UpdateTransactionJournalReadStatus.Active || current.Journal is null ||
            !IsValid(journal, journal.OperationId) ||
            !string.Equals(current.Journal.InstallIdentity, journal.InstallIdentity, StringComparison.Ordinal))
        {
            return UpdateTransactionJournalWriteStatus.Invalid;
        }
        return Write(journal, replace: true);
    }

    private UpdateTransactionJournalWriteStatus Write(UpdateTransactionJournal journal, bool replace)
    {
        var destination = GetJournalFilePath(journal.OperationId);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(paths.DirectoryPath);
            if (!replace && (File.Exists(destination) || Directory.Exists(destination)))
            {
                return UpdateTransactionJournalWriteStatus.AlreadyExists;
            }

            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, journal, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: replace);
            return replace ? UpdateTransactionJournalWriteStatus.Updated : UpdateTransactionJournalWriteStatus.Created;
        }
        catch (IOException exception) when (!replace && IsAlreadyExists(exception))
        {
            return UpdateTransactionJournalWriteStatus.AlreadyExists;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            TryDelete(temporary);
            return UpdateTransactionJournalWriteStatus.StorageFailure;
        }
    }

    private bool IsValid(UpdateTransactionJournal journal, Guid expectedOperationId)
    {
        if (journal.SchemaVersion != 1 ||
            !string.Equals(journal.ProductId, InstallIdentity.ProductIdValue, StringComparison.Ordinal) ||
            !string.Equals(journal.InstallIdentity, paths.InstallIdentity, StringComparison.Ordinal) ||
            journal.OperationId == Guid.Empty || journal.OperationId != expectedOperationId ||
            !Enum.IsDefined(journal.Phase) ||
            !SemVersion.TryParse(journal.TargetVersion, out var target) || !target!.IsStable ||
            journal.CreatedUtc.Offset != TimeSpan.Zero || journal.UpdatedUtc.Offset != TimeSpan.Zero ||
            journal.UpdatedUtc < journal.CreatedUtc ||
            !IsAbsolutePath(journal.InstallRoot) || !IsAbsolutePath(journal.VerifiedExtractRoot))
        {
            return false;
        }

        try
        {
            var expected = UpdateTransactionPaths.ForInstall(journal.InstallRoot, journal.OperationId);
            var identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, expected.InstallRoot);
            return string.Equals(identity.Key, paths.InstallIdentity, StringComparison.Ordinal) &&
                PathsEqual(journal.InstallRoot, expected.InstallRoot) &&
                PathsEqual(journal.NewSiblingPath, expected.NewSiblingPath) &&
                PathsEqual(journal.BackupSiblingPath, expected.BackupSiblingPath) &&
                PathsEqual(journal.VerifiedExtractRoot, Path.GetFullPath(journal.VerifiedExtractRoot)) &&
                (journal.LaunchedAppIdentity is null ||
                    (journal.LaunchedAppIdentity.ProcessId > 0 && journal.LaunchedAppIdentity.StartedUtc.Offset == TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsAbsolutePath(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static bool IsAlreadyExists(IOException exception) => (exception.HResult & 0xFFFF) is 17 or 80 or 183;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
