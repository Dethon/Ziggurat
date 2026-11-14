using System.Collections.Concurrent;

namespace McpChannelSignalR.Services;

// How many chat-client connections each account holds right now, wherever they are. A question
// offered to a person with none open goes to their devices instead, and SignalR keeps no count of a
// group's members to ask.
public sealed class AccountConnections
{
    private readonly ConcurrentDictionary<string, int> _open = new(StringComparer.Ordinal);

    public void Opened(string userId) => _open.AddOrUpdate(userId, 1, (_, count) => count + 1);

    public void Closed(string userId) => _open.AddOrUpdate(userId, 0, (_, count) => Math.Max(0, count - 1));

    public bool AnyOpen(string userId) => _open.TryGetValue(userId, out var count) && count > 0;
}