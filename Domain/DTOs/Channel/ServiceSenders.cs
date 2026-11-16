namespace Domain.DTOs.Channel;

// The words a turn nobody typed goes by: a schedule's fire, a watch's, a login wake, an estimate the
// Sugary app asks for, a system notice. Such a turn may act for a person — its delegation or the
// app's assertion says whom — and is billed to them, but its sender is its server's word, never their
// name, so nothing that labels a person by the names their calls carried may take one of these.
public static class ServiceSenders
{
    public const string Scheduler = "scheduler";
    public const string Watch = "watch";
    public const string Login = "login";
    public const string SugaryApp = "sugary-app";

    private static readonly HashSet<string> _all = [Scheduler, Watch, Login, SugaryApp, ChannelProtocol.SystemSender];

    public static bool IsService(string sender) => _all.Contains(sender);
}