using Domain.DTOs.Sugary;

namespace McpServerSugary.Store;

// Who she is, as she told the app: one row per person, every field empty until she says it. Whether
// she is a woman or a man decides whether the app shows her the Cycle tab; the rest is what the agent
// reads beside her log.
public class Profile : IPersonal
{
    public string UserId { get; set; } = "";
    public string TenantId { get; set; } = "";

    public Sex? Sex { get; set; }
    public DiabetesType? DiabetesType { get; set; }
    public DateOnly? DiagnosedOn { get; set; }
}

// A height or a weight she recorded on a day, kept as a history: a child with diabetes grows, and a
// dose that suited them last year may not now. One of each kind a day.
public class BodyMeasurement : IPersonal
{
    public string UserId { get; set; } = "";
    public string TenantId { get; set; } = "";

    public Guid Id { get; set; }
    public BodyMeasure Kind { get; set; }
    public DateOnly On { get; set; }

    // Centimetres for a height, kilograms for a weight.
    public double Value { get; set; }
}