namespace McpServerSugary.Store;

// Whether she wants her phones told before her period, and which expected start they were last told
// of. Hers to turn on: no row, or Enabled false, is off — it shows on a lock screen.
public class CycleReminder : IPersonal
{
    public string UserId { get; set; } = "";
    public string TenantId { get; set; } = "";

    public bool Enabled { get; set; }

    // The likeliest start a phone of hers took a reminder for; a new forecast is a new reminder.
    public DateOnly? SentFor { get; set; }
}