using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpServerApps.Settings;

namespace McpServerApps.Asks;

// The conversations this server opened for an app's asks, each bound to the app and the viewer who asked:
// the only ones a follow-up may continue, and only from that app and that viewer, so another app or
// member never carries on somebody else's conversation as their own. A restart forgets them all, and the
// app then asks afresh.
public sealed class AskConversations(TimeProvider clock, AppsSettings settings)
{
    private sealed record Asker(string AppId, Account Viewer);

    // The agent each conversation's turns went to: the agent groups messages by conversation and agent, so
    // a follow-up names the one that holds the conversation's history.
    private readonly OpenConversations<Asker, string> _open = new(clock, settings.Asks.FollowUpWindow);

    public string Open(string appId, Account viewer, string agentId) => _open.Open(new Asker(appId, viewer), agentId);
}