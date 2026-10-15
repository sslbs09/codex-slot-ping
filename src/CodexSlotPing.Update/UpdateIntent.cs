namespace CodexSlotPing.Update;

public sealed record UpdateIntent(
    int SchemaVersion,
    string ProductId,
    string InstallIdentity,
    Guid OperationId,
    int UpdaterPid,
    DateTimeOffset UpdaterStartedUtc,
    DateTimeOffset CreatedUtc);

public enum UpdateIntentCreateStatus
{
    Created,
    AlreadyExists,
    StorageFailure
}

public enum UpdateIntentReadStatus
{
    Missing,
    Active,
    Invalid,
    IoError
}

public sealed record UpdateIntentReadResult(
    UpdateIntentReadStatus Status,
    UpdateIntent? Intent,
    string? Error)
{
    public static UpdateIntentReadResult Missing() => new(UpdateIntentReadStatus.Missing, null, null);
    public static UpdateIntentReadResult Active(UpdateIntent intent) => new(UpdateIntentReadStatus.Active, intent, null);
    public static UpdateIntentReadResult Invalid(string error) => new(UpdateIntentReadStatus.Invalid, null, error);
    public static UpdateIntentReadResult IoError(string error) => new(UpdateIntentReadStatus.IoError, null, error);
}

public interface IUpdateIntentReader
{
    UpdateIntentReadResult Read();
}

public interface IUpdateIntentLifecycle : IUpdateIntentReader
{
    bool TryDelete(Guid operationId);
}
