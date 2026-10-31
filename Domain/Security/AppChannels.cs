namespace Domain.Security;

// What the proxy's session check names (`?channel=`) for an app that starts no turn, which the
// assertion it signs then names, and which the app holds it to. The apps that do start turns are
// named by their channel (ChannelIds, SugaryChannel.ChannelId).
public static class AppChannels
{
    // The operator's dashboard, the Observability host.
    public const string Dashboard = "dashboard";

    // SexyTime, whose server forwards the assertion it was handed to the agent registration API.
    public const string SexyTime = "sexytime";
}