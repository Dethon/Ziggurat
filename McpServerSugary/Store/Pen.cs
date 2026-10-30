using Domain.DTOs.Sugary;

namespace McpServerSugary.Store;

// An insulin pen she opened: which insulin, the day she first used it and how many days it lasts
// once open. One pen of each kind is open at a time; a replaced one is kept with the day it was
// replaced, as her history of pens.
public class Pen : IPersonal
{
    public string UserId { get; set; } = "";
    public string TenantId { get; set; } = "";

    public Guid Id { get; set; }
    public DoseKind Kind { get; set; }
    public string Insulin { get; set; } = "";
    public DateOnly OpenedOn { get; set; }
    public int Days { get; set; }
    public DateOnly? ReplacedOn { get; set; }

    // The replacement day each reminder was last sent for. Keyed by the day rather than a flag, so a
    // pen whose dates she corrects is reminded again for its new day, and a restart never resends.
    public DateOnly? TomorrowSentFor { get; set; }
    public DateOnly? TodaySentFor { get; set; }

    public DateOnly ReplaceOn => OpenedOn.AddDays(Days);

    public void Sent(PenReminder reminder)
    {
        if (reminder == PenReminder.Tomorrow)
        {
            TomorrowSentFor = ReplaceOn;
        }
        else
        {
            TodaySentFor = ReplaceOn;
        }
    }
}

public enum PenReminder
{
    Tomorrow,
    Today
}