namespace Domain.Extensions;

public static class TimeZones
{
    // A household's zone by its IANA name, or none for no name or one this host's time zone database
    // does not know — accounts checked the name against a list of its own when it was saved, so a
    // reader falls back to its own zone rather than failing.
    public static TimeZoneInfo? Named(string? id) =>
        id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
}