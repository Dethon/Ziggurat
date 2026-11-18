namespace Domain.Tools.Watches;

// The one mount point every server's watches share, and how a new watch finds the server that keeps
// it: a union (FileSystemUnion) routed by the kinds of its triggers. The home's watches take every
// kind nobody claims; Sugary's take the glucose ones.
public static class Watchers
{
    public const string MountPoint = "/watchers";
    public const string RouteBy = "/triggers/*/trigger";
    public const string WatchFileName = "watch.json";
    public const string StatusFileName = "status.json";
}