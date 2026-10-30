namespace McpServerSugary.Store;

// A phone that asked to be told when a pen is due: what its browser's push service gave it, and the
// language the app on it is set to, which only the phone knows.
public class PushSubscription : IPersonal
{
    public string UserId { get; set; } = "";
    public string TenantId { get; set; } = "";

    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public string Language { get; set; } = "es";
}