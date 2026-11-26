using static Dashboard.Client.Components.PillSelector;

namespace Dashboard.Client.Components;

// The time pills every page shows, each the last that many days to the minute (the server resolves
// ?days= against its own clock on every read). A calendar "Today" covered a few minutes just after
// midnight and nearly a whole day just before the next one. One list, so the Overview and the
// breakdown pages cannot drift apart again.
public static class TimeWindows
{
    public static readonly IReadOnlyList<PillOption> Options =
    [
        new("24h", "1"),
        new("7d", "7"),
        new("30d", "30"),
    ];
}