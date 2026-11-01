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

    // The admin API on the accounts service itself (/accounts/admin/*), which verifies its own
    // assertions: one of its own, so an assertion the hub or an app was handed opens no admin route.
    public const string Admin = "admin";
}