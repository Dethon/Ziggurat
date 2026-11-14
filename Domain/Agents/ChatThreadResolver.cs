using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.Conversations;

namespace Domain.Agents;

public sealed class ChatThreadResolver(IThreadStateStore? threadStateStore = null) : IDisposable
{
    private readonly ConcurrentDictionary<AgentKey, ChatThreadContext> _contexts = [];
    private readonly Lock _lock = new();
    private int _isDisposed;

    public IEnumerable<AgentKey> AgentKeys => _contexts.Keys;

    // The household a conversation's topic records, where the agent keeps one over it (an id shaped as a
    // topic's, ConversationIdentity): a conversation is that household's, and no other household's
    // server may join it or have its history read. None for a conversation no topic holds.
    public async Task<string?> HouseholdOfAsync(AgentKey key, CancellationToken ct)
    {
        if (threadStateStore is null || ConversationIdentity.Parse(key.ConversationId) is not { } id)
        {
            return null;
        }

        var topic = await threadStateStore.GetTopicByChatIdAndThreadIdAsync(
            key.AgentId ?? "default", id.ChatId, id.ThreadId, ct);
        return topic?.TenantId;
    }

    public ChatThreadContext Resolve(AgentKey key)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        lock (_lock)
        {
            if (_contexts.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var context = new ChatThreadContext();
            _contexts[key] = context;
            return context;
        }
    }

    // Addressed at the context the caller resolved, never at "whatever is under this key now".
    // A conversation group torn down by a /cancel can still be inside its establish path when
    // it fails, and by then the next message has opened a fresh group with a fresh context
    // under the same key. Disposing that one would kill the successor's turn and drop the
    // user's new message, so a stale caller is a no-op.
    public void Cancel(AgentKey key, ChatThreadContext expected)
    {
        if (_isDisposed != 0)
        {
            return;
        }

        lock (_lock)
        {
            if (!_contexts.TryGetValue(key, out var context) || !ReferenceEquals(context, expected))
            {
                return;
            }

            _contexts.Remove(key, out _);
        }

        // Outside the lock: disposing runs the group's completion callback.
        expected.Dispose();
    }

    public async Task ClearAsync(AgentKey key)
    {
        if (_isDisposed != 0)
        {
            return;
        }

        // The delete does not depend on finding a live context. A /cancel arriving just before
        // the /clear already removed and disposed it, and a /clear on a conversation with no live
        // group is routine after a restart; in both cases the user asked for the history to be
        // gone. The store delete is one idempotent key delete, so doing it either way costs a
        // round trip and never leaves the cleared history behind.
        if (_contexts.Remove(key, out var context))
        {
            context.Dispose();
        }

        await DeletePersistedStateAsync(key);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var context in _contexts.Values)
            {
                context.Dispose();
            }
        }

        _contexts.Clear();
    }

    private async Task DeletePersistedStateAsync(AgentKey key)
    {
        if (threadStateStore is null)
        {
            return;
        }

        await threadStateStore.DeleteAsync(key);
    }
}