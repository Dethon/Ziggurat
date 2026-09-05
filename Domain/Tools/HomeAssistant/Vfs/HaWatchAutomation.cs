namespace Domain.Tools.HomeAssistant.Vfs;

// The marker that makes an automation a watch: its config id starts with this. Hand-made
// automations — the alarm bridge, blueprints — carry no prefix and are invisible to `/ha/watches`,
// while still appearing under `/ha/entities/automation/` as entities.
public static class HaWatchAutomation
{
    public const string IdPrefix = "assistant_watch_";

    public static string AutomationId(string watchId) => IdPrefix + watchId;

    public static bool IsWatch(string? automationId) =>
        automationId is not null && automationId.StartsWith(IdPrefix, StringComparison.Ordinal);

    public static string WatchId(string automationId) => automationId[IdPrefix.Length..];
}