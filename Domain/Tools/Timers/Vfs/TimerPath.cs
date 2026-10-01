namespace Domain.Tools.Timers.Vfs;

public enum TimerNodeKind
{
    Root, TimerDir, TimerFile, StatusFile, DismissFile, Unknown
}

public sealed record TimerNode(TimerNodeKind Kind, string? TimerId);

public static class TimerPath
{
    public const string TimerFileName = "timer.json";
    public const string StatusFileName = "status.json";
    // An action file, so it carries no extension: it runs, it is never a script to open, and a
    // real shell will run it as `./dismiss` once exec reaches the sandbox.
    public const string DismissFileName = "dismiss";

    public static TimerNode Parse(string path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (Array.Exists(segments, s => s is "." or ".."))
        {
            return new TimerNode(TimerNodeKind.Unknown, null);
        }

        return segments switch
        {
            [] => new TimerNode(TimerNodeKind.Root, null),
            [DismissFileName] => new TimerNode(TimerNodeKind.DismissFile, null),
            [var id] when !IsReserved(id) => new TimerNode(TimerNodeKind.TimerDir, id),
            [var id, TimerFileName] when !IsReserved(id) => new TimerNode(TimerNodeKind.TimerFile, id),
            [var id, StatusFileName] when !IsReserved(id) => new TimerNode(TimerNodeKind.StatusFile, id),
            _ => new TimerNode(TimerNodeKind.Unknown, null)
        };
    }

    private static bool IsReserved(string segment) =>
        segment is TimerFileName or StatusFileName or DismissFileName;
}