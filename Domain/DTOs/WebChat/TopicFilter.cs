namespace Domain.DTOs.WebChat;

// Which of the topics a person sees their list shows, for whoever sees every topic of the household
// and would rather not read them all: their own (Mine), everyone's, or one person's. It only ever
// narrows — TopicViewer.Lists asks it beside Sees, never instead — so it needs no permission of its own
// and a client cannot widen what it is shown by naming one.
public sealed record TopicFilter(bool MineOnly = false, string? OwnerId = null)
{
    public static readonly TopicFilter Everyone = new();

    public static readonly TopicFilter Mine = new(MineOnly: true);

    public static TopicFilter Of(string ownerId) => new(OwnerId: ownerId);

    // Mine is what the person would see were they not granted everyone's: their own, the household's
    // shared ones and the ones nobody owns.
    public bool Admits(TopicMetadata topic, TopicViewer viewer) =>
        MineOnly
            ? (viewer with { SeesAll = false }).Sees(topic)
            : OwnerId is null || topic.OwnerId == OwnerId;
}