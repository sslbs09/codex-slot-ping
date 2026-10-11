namespace CodexSlotPing.Core.Slots;

public enum SlotAdmissionKind
{
    Eligible,
    Pending,
    Missed,
    Blocked
}

public enum AdmissionBlockReason
{
    InvalidConfiguration,
    TimeZoneMismatch,
    TimeZoneUnavailable
}

public sealed record SlotAdmissionResult(
    SlotAdmissionKind Kind,
    FormalSlotStatus? FormalStatus,
    SlotId? SlotId,
    int? Position,
    AdmissionBlockReason? BlockReason);
