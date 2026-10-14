using System.Collections.Concurrent;

namespace McpServerSugary.Channel;

// The conversations this server opened for a meal's first estimate, and so the only ones her
// answers may continue. The agent would carry on any conversation id it is sent, and one it has
// never seen holds neither her photos nor her words, so the answer would be an estimate of nothing.
// Kept in memory: a restart forgets them, and the app then estimates the meal afresh.
public sealed class OpenEstimates
{
    private readonly ConcurrentDictionary<string, byte> _open = new();

    public string Open()
    {
        var conversation = Guid.NewGuid().ToString("N");
        _open[conversation] = 0;
        return conversation;
    }

    public bool IsOpen(string conversation) => _open.ContainsKey(conversation);
}