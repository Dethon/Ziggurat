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

    // The zone of its person's household when the app last saved it — on every open — so the pen
    // reminders, which wake for nobody's request, come at the reminder hour on that household's clock.
    // Null for one saved before it was kept: the server's own zone.
    public string? TimeZone { get; set; }

    // The signed-in session that saved it, from the request's assertion: once that session ends —
    // signed out from the account page, its person disabled — the phone is told nothing and dropped.
    // Null for one saved before it was kept, told as before until it is saved again.
    public string? SessionId { get; set; }
}