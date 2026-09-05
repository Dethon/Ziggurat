namespace Domain.DTOs;

// Where an unprompted fire lands when its author named no channel. Two servers raise fires — the
// scheduler and the Home Assistant server's watch callback — and they must agree, or the same
// person's warnings land in different places depending on which server raised them. So the default
// is one shared file rather than a block in each host's appsettings.json, the retention policy's
// shape: shipped beside this type in Domain, copied into every referencing host's output, read
// after each host's appsettings.json and before environment variables.
public record DeliverySettings
{
    public const string FileName = "delivery.json";

    // Channel ids, `channelId[:address]` each, exactly as a schedule's `deliverTo` spells them.
    public IReadOnlyList<string> DefaultDeliverTo { get; init; } = [];
}