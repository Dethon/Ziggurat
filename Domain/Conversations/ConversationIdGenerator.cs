namespace Domain.Conversations;

public static class ConversationIdGenerator
{
    public static string NewTopicId() => Guid.NewGuid().ToString("N");

    public static ConversationIdentity CreateFor(string topicId)
    {
        var chatId = GetDeterministicHash(topicId, seed: 0x1234);
        var threadId = GetDeterministicHash(topicId, seed: 0x5678) & 0x7FFFFFFF;
        return new ConversationIdentity(chatId, threadId);
    }

    private static long GetDeterministicHash(string input, long seed)
    {
        const long fnvPrime = 0x100000001b3;
        var hash = unchecked((long)0xcbf29ce484222325) ^ seed;

        foreach (var c in input)
        {
            hash ^= c;
            hash = unchecked(hash * fnvPrime);
        }

        return hash & 0x7FFFFFFFFFFFFFFF;
    }
}