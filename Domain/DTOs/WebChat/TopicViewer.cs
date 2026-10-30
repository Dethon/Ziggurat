namespace Domain.DTOs.WebChat;

// Somebody looking at a household's topics, and the one rule for which of them they may know
// exist: their own, every shared one of their household, every one nobody owns that is their
// household's or no household's, and — for whoever an owner granted "sees all conversations" — every
// one of their household. The hub lists, reads and acts by it, the store pages by it and the chat
// client drops a row by it, so the three cannot disagree about who sees what.
public sealed record TopicViewer(string UserId, string TenantId, bool SeesAll = false)
{
    // A topic nobody owns still records the household it was made for when there was one (a Telegram
    // group's copy, the household the group is bound to), and is that household's alone; one recording
    // none (a voice guest's, one from before topics had owners) is everyone's in its space.
    public bool Sees(TopicMetadata topic) =>
        topic.OwnerId is null && (topic.TenantId is null || topic.TenantId == TenantId)
        || Owns(topic)
        || topic.TenantId == TenantId && (topic.Shared || SeesAll);

    public bool Owns(TopicMetadata topic) => topic.OwnerId == UserId;

    // Writing in a topic — continuing it, stopping its reply, attaching to it, marking it read — is
    // for whoever it is shared with. Seeing every topic is reading them, never deciding in them: a
    // private topic is its owner's alone to change, whoever else may read it.
    public bool MayContinue(TopicMetadata topic) =>
        Owns(topic) || Sees(topic) && (topic.OwnerId is null || topic.Shared);

    // What a topic is called and whether it exists are its owner's, shared or not: a member continuing
    // it works in it and does not decide it away. A topic nobody owns has nobody to defer to, so
    // whoever sees it may, as everyone could before topics had owners.
    public bool MayRenameOrDelete(TopicMetadata topic) =>
        topic.OwnerId is null ? Sees(topic) : Owns(topic);
}