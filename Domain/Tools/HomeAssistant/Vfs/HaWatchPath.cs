namespace Domain.Tools.HomeAssistant.Vfs;

public enum HaWatchKind
{
    Root, WatchDir, WatchFile, WatchStatusFile, Unknown
}

public sealed record HaWatchNode(HaWatchKind Kind, string? WatchId = null);

// A path on the home's watch mount (/watchers), relative to it: `<id>/watch.json`, with a read-only
// `status.json` beside it.
public static class HaWatchPath
{
    public const string WatchFileName = "watch.json";
    public const string StatusFileName = "status.json";

    public static HaWatchNode Parse(string relativePath)
    {
        var s = (relativePath ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return s.Length switch
        {
            0 => new HaWatchNode(HaWatchKind.Root),
            1 => new HaWatchNode(HaWatchKind.WatchDir, s[0]),
            2 when s[1] == WatchFileName => new HaWatchNode(HaWatchKind.WatchFile, s[0]),
            2 when s[1] == StatusFileName => new HaWatchNode(HaWatchKind.WatchStatusFile, s[0]),
            _ => new HaWatchNode(HaWatchKind.Unknown)
        };
    }
}