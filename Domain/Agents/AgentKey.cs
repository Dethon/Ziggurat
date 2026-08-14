namespace Domain.Agents;

public readonly record struct AgentKey(string ConversationId, string? AgentId = null)
{
    public override string ToString()
    {
        return $"agent-key:{AgentId}:{ConversationId}";
    }

    // The inverse of ToString for a chat conversation, whose conversation id is
    // "{chatId}:{threadId}". An agent id may itself carry ':', so the two numeric segments are
    // read from the end. Anything else — the GUID a session falls back to when it has no key
    // yet — is no agent key and yields nothing.
    public static (string AgentId, long ChatId, long ThreadId)? ChatConversationParts(string rendered)
    {
        const string prefix = "agent-key:";
        if (!rendered.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = rendered[prefix.Length..].Split(':');
        return parts.Length >= 3
               && long.TryParse(parts[^2], out var chatId)
               && long.TryParse(parts[^1], out var threadId)
            ? (string.Join(':', parts[..^2]), chatId, threadId)
            : null;
    }
}