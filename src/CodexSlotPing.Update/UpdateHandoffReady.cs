using System.Text.Json;

namespace CodexSlotPing.Update;

public sealed record UpdateHandoffReady(
    int SchemaVersion,
    string ProductId,
    string InstallIdentity,
    Guid OperationId,
    int UpdaterPid,
    DateTimeOffset UpdaterStartedUtc,
    DateTimeOffset CreatedUtc);

public enum UpdateHandoffReadyReadStatus { Missing, Active, Invalid, IoError }
public enum UpdateHandoffReadyWriteStatus { Created, AlreadyExists, Invalid, StorageFailure }

public sealed record UpdateHandoffReadyReadResult(
    UpdateHandoffReadyReadStatus Status,
    UpdateHandoffReady? HandoffReady,
    string? Error);

public interface IUpdateHandoffReadyStore
{
    UpdateHandoffReadyReadResult Read(Guid operationId);
    UpdateHandoffReadyWriteStatus TryPublish(UpdateHandoffReady handoffReady);
    bool TryDelete(Guid operationId);
}

public sealed class UpdateHandoffReadyStore(UpdateControlPaths paths) : IUpdateHandoffReadyStore
{
    private static readonly HashSet<string> Properties = new(
        ["schemaVersion", "productId", "installIdentity", "operationId", "updaterPid", "updaterStartedUtc", "createdUtc"],
        StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public UpdateHandoffReadyReadResult Read(Guid operationId)
    {
        if (operationId == Guid.Empty) return Invalid("operation-id-invalid");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(paths.HandoffReadyFilePath(operationId)));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Invalid("handoff-root-invalid");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!Properties.Contains(property.Name) || !seen.Add(property.Name)) return Invalid("handoff-properties-invalid");
            }
            if (seen.Count != Properties.Count) return Invalid("handoff-properties-incomplete");
            var marker = JsonSerializer.Deserialize<UpdateHandoffReady>(document.RootElement.GetRawText(), JsonOptions);
            return marker is not null && IsValid(marker, operationId)
                ? new(UpdateHandoffReadyReadStatus.Active, marker, null)
                : Invalid("handoff-contract-invalid");
        }
        catch (FileNotFoundException) { return Missing(); }
        catch (DirectoryNotFoundException) { return Missing(); }
        catch (JsonException) { return Invalid("handoff-json-invalid"); }
        catch (UnauthorizedAccessException) { return IoError("handoff-access-denied"); }
        catch (IOException) { return IoError("handoff-read-failed"); }
    }

    public UpdateHandoffReadyWriteStatus TryPublish(UpdateHandoffReady handoffReady)
    {
        ArgumentNullException.ThrowIfNull(handoffReady);
        if (!IsValid(handoffReady, handoffReady.OperationId)) return UpdateHandoffReadyWriteStatus.Invalid;

        var destination = paths.HandoffReadyFilePath(handoffReady.OperationId);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(paths.DirectoryPath);
            if (File.Exists(destination) || Directory.Exists(destination)) return UpdateHandoffReadyWriteStatus.AlreadyExists;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, handoffReady, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination);
            return UpdateHandoffReadyWriteStatus.Created;
        }
        catch (IOException exception) when (IsAlreadyExists(exception))
        {
            TryDeleteFile(temporary);
            return UpdateHandoffReadyWriteStatus.AlreadyExists;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            TryDeleteFile(temporary);
            return UpdateHandoffReadyWriteStatus.StorageFailure;
        }
    }

    public bool TryDelete(Guid operationId)
    {
        if (operationId == Guid.Empty) return false;
        try
        {
            File.Delete(paths.HandoffReadyFilePath(operationId));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private bool IsValid(UpdateHandoffReady marker, Guid expectedOperationId) =>
        marker.SchemaVersion == 1 &&
        string.Equals(marker.ProductId, InstallIdentity.ProductIdValue, StringComparison.Ordinal) &&
        string.Equals(marker.InstallIdentity, paths.InstallIdentity, StringComparison.Ordinal) &&
        marker.OperationId != Guid.Empty && marker.OperationId == expectedOperationId &&
        marker.UpdaterPid > 0 && marker.UpdaterStartedUtc.Offset == TimeSpan.Zero && marker.CreatedUtc.Offset == TimeSpan.Zero;

    private static bool IsAlreadyExists(IOException exception) => (exception.HResult & 0xFFFF) is 17 or 80 or 183;
    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    private static UpdateHandoffReadyReadResult Missing() => new(UpdateHandoffReadyReadStatus.Missing, null, null);
    private static UpdateHandoffReadyReadResult Invalid(string error) => new(UpdateHandoffReadyReadStatus.Invalid, null, error);
    private static UpdateHandoffReadyReadResult IoError(string error) => new(UpdateHandoffReadyReadStatus.IoError, null, error);
}

public static class UpdateHandoffReadyValidator
{
    public static bool Matches(UpdateHandoffReady? marker, Guid operationId, string installIdentity, ProcessIdentity launchedUpdater) =>
        marker is not null && operationId != Guid.Empty && launchedUpdater is not null &&
        marker.SchemaVersion == 1 &&
        string.Equals(marker.ProductId, InstallIdentity.ProductIdValue, StringComparison.Ordinal) &&
        string.Equals(marker.InstallIdentity, installIdentity, StringComparison.Ordinal) &&
        marker.OperationId == operationId &&
        marker.UpdaterPid == launchedUpdater.ProcessId &&
        marker.UpdaterStartedUtc == launchedUpdater.StartedUtc;
}
