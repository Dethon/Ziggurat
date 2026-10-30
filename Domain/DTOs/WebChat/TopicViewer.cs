namespace Domain.DTOs.WebChat;

// Somebody looking at a household's topics, and the one rule for which of them they may know
// exist: their own, every shared one of their household, every one nobody owns, and — for whoever
// an owner granted "sees all conversations" — every one of their household. The hub lists, reads
// and acts by it, the store pages by it and the chat client drops a row by it, so the three cannot
// disagree about who sees what.
public sealed record TopicViewer(string UserId, string TenantId, bool SeesAll = false)
{
    public bool Sees(TopicMetadata topic) =>
        topic.OwnerId is null
        || Owns(topic)
        || topic.TenantId == TenantId && (topic.Shared || SeesAll);

    public bool Owns(TopicMetadata topic) => topic.OwnerId == UserId;

    // Writing in a topic — continuing it, renaming it, deleting it, stopping its reply — is for
    // whoever it is shared with. Seeing every topic is reading them, never deciding in them: a
    // private topic is its owner's alone to change, whoever else may read it.
    public bool MayChange(TopicMetadata topic) =>
        topic.OwnerId is null
        || Owns(topic)
        || topic.TenantId == TenantId && topic.Shared;
}