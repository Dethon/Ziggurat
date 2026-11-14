namespace WebChat.Client.Contracts;

// Which conversation this window had open in a space, kept for the window alone: the app loaded again
// in it — back from a page the agent sent, reloaded — opens it again, while a fresh start opens as ever.
public interface IOpenTopicMemory
{
    ValueTask<string?> RecallAsync(string slug);

    // Null forgets: a new chat has nothing to come back to.
    ValueTask RememberAsync(string slug, string? topicId);
}