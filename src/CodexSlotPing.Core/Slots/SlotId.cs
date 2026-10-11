namespace CodexSlotPing.Core.Slots;

public sealed record SlotId(string WindowsTimeZoneId, DateOnly LocalDate, TimeOnly LocalTime)
{
    public override string ToString() => FormattableString.Invariant($"{LocalDate:yyyy-MM-dd}/{LocalTime:HH:mm}@{WindowsTimeZoneId}");
}
