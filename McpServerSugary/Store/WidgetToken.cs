namespace McpServerSugary.Store;

// What lets a home screen widget, which can hold no session and send no header, read one log's glucose.
// The row is its signer's (UserId, TenantId); LogUserId is whose glucose it reads, somebody else's only
// for a holder of everyone's log. Only the token's hash is kept, so the database alone opens nothing,
// and it lives no longer than the session that made it (WidgetAccess).
public class WidgetToken : IPersonal
{
    public string UserId { get; set; } = "";
    public string TenantId { get; set; } = "";

    public string Hash { get; set; } = "";
    public string LogUserId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}