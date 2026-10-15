using System.Text.Json;

namespace CodexSlotPing.Update;

public sealed record AppStartupAcknowledgement(
    int SchemaVersion,
    string ProductId,
    string InstallIdentity,
    Guid OperationId,
    string RunningVersion,
    int AppPid,
    DateTimeOffset AppStartedUtc);

public enum AppStartupAcknowledgementReadStatus
{
    Missing,
    Active,
    Invalid,
    IoError
}

public sealed record AppStartupAcknowledgementReadResult(
    AppStartupAcknowledgementReadStatus Status,
    AppStartupAcknowledgement? Acknowledgement,
    string? Error);

public enum AppStartupAcknowledgementWriteStatus
{
    Created,
    AlreadyExists,
    Invalid,
    StorageFailure
}

public interface IAppStartupAcknowledgementStore
{
    string GetAcknowledgementFilePath(Guid operationId);
    AppStartupAcknowledgementReadResult Read(Guid operationId);
    AppStartupAcknowledgementWriteStatus TryPublish(AppStartupAcknowledgement acknowledgement);
}

public sealed class AppStartupAcknowledgementStore(UpdateControlPaths paths) : IAppStartupAcknowledgementStore
{
    private static readonly HashSet<string> Properties = new(
        ["schemaVersion", "productId", "installIdentity", "operationId", "runningVersion", "appPid", "appStartedUtc"],
        StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string GetAcknowledgementFilePath(Guid operationId) => paths.StartupAcknowledgementFilePath(operationId);

    public AppStartupAcknowledgementReadResult Read(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            return Invalid("operation-id-invalid");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(GetAcknowledgementFilePath(operationId)));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("ack-root-invalid");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!Properties.Contains(property.Name) || !seen.Add(property.Name))
                {
                    return Invalid("ack-properties-invalid");
                }
            }
            if (seen.Count != Properties.Count)
            {
                return Invalid("ack-properties-incomplete");
            }

            var acknowledgement = JsonSerializer.Deserialize<AppStartupAcknowledgement>(document.RootElement.GetRawText(), JsonOptions);
            return acknowledgement is not null && IsValid(acknowledgement, operationId)
                ? new AppStartupAcknowledgementReadResult(AppStartupAcknowledgementReadStatus.Active, acknowledgement, null)
                : Invalid("ack-contract-invalid");
        }
        catch (FileNotFoundException)
        {
            return new AppStartupAcknowledgementReadResult(AppStartupAcknowledgementReadStatus.Missing, null, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new AppStartupAcknowledgementReadResult(AppStartupAcknowledgementReadStatus.Missing, null, null);
        }
        catch (JsonException)
        {
            return Invalid("ack-json-invalid");
        }
        catch (UnauthorizedAccessException)
        {
            return IoError("ack-access-denied");
        }
        catch (IOException)
        {
            return IoError("ack-read-failed");
        }
    }

    public AppStartupAcknowledgementWriteStatus TryPublish(AppStartupAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (!IsValid(acknowledgement, acknowledgement.OperationId))
        {
            return AppStartupAcknowledgementWriteStatus.Invalid;
        }

        var destination = GetAcknowledgementFilePath(acknowledgement.OperationId);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(paths.DirectoryPath);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                return AppStartupAcknowledgementWriteStatus.AlreadyExists;
            }

            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, acknowledgement, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination);
            return AppStartupAcknowledgementWriteStatus.Created;
        }
        catch (IOException exception) when (IsAlreadyExists(exception))
        {
            TryDelete(temporary);
            return AppStartupAcknowledgementWriteStatus.AlreadyExists;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            TryDelete(temporary);
            return AppStartupAcknowledgementWriteStatus.StorageFailure;
        }
    }

    private bool IsValid(AppStartupAcknowledgement acknowledgement, Guid expectedOperationId) =>
        acknowledgement.SchemaVersion == 1 &&
        string.Equals(acknowledgement.ProductId, InstallIdentity.ProductIdValue, StringComparison.Ordinal) &&
        string.Equals(acknowledgement.InstallIdentity, paths.InstallIdentity, StringComparison.Ordinal) &&
        acknowledgement.OperationId != Guid.Empty && acknowledgement.OperationId == expectedOperationId &&
        SemVersion.TryParse(acknowledgement.RunningVersion, out var version) && version!.IsStable &&
        acknowledgement.AppPid > 0 && acknowledgement.AppStartedUtc.Offset == TimeSpan.Zero;

    private static bool IsAlreadyExists(IOException exception) => (exception.HResult & 0xFFFF) is 17 or 80 or 183;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static AppStartupAcknowledgementReadResult Invalid(string error) =>
        new(AppStartupAcknowledgementReadStatus.Invalid, null, error);

    private static AppStartupAcknowledgementReadResult IoError(string error) =>
        new(AppStartupAcknowledgementReadStatus.IoError, null, error);
}

public static class AppStartupAcknowledgementValidator
{
    public static bool Matches(
        AppStartupAcknowledgement? acknowledgement,
        Guid operationId,
        string installIdentity,
        SemVersion targetVersion,
        ProcessIdentity launchedAppIdentity) =>
        acknowledgement is not null && operationId != Guid.Empty && targetVersion is not null &&
        string.Equals(acknowledgement.ProductId, InstallIdentity.ProductIdValue, StringComparison.Ordinal) &&
        string.Equals(acknowledgement.InstallIdentity, installIdentity, StringComparison.Ordinal) &&
        acknowledgement.OperationId == operationId && acknowledgement.SchemaVersion == 1 &&
        SemVersion.TryParse(acknowledgement.RunningVersion, out var runningVersion) &&
        runningVersion!.IsStable && string.Equals(runningVersion.ToString(), targetVersion.ToString(), StringComparison.Ordinal) &&
        acknowledgement.AppPid == launchedAppIdentity.ProcessId &&
        acknowledgement.AppStartedUtc == launchedAppIdentity.StartedUtc;
}
