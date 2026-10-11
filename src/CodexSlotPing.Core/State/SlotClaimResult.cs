using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.Configuration;

namespace CodexSlotPing.Core.State;

public enum SlotClaimKind
{
    Claimed,
    NotEligible,
    AlreadyConsumed,
    Blocked
}

public enum SlotClaimBlockReason
{
    MutexUnavailable,
    ConfigurationDisabled,
    InvalidConfigOrState,
    ClockRollback,
    TimeZoneMismatch,
    TimeZoneUnavailable,
    InvalidConfiguration,
    StatePersistenceFailed
}

public sealed record SlotClaimResult(
    SlotClaimKind Kind,
    SlotId? SlotId,
    FormalSlotStatus? Status,
    SlotClaimBlockReason? BlockReason)
{
    public bool IsClaimed => Kind == SlotClaimKind.Claimed;

    public AppConfig? ConfigSnapshot { get; init; }
}
