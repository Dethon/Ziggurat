using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpServerApps.Settings;

namespace McpServerApps.Asks;

// The conversations this server opened for an app's asks, each bound to the app and the viewer who asked:
// the only ones a follow-up may continue, and only from that app and that viewer, so another app or
// member never carries on somebody else's conversation as their own. A restart forgets them all, and the
// app then asks afresh: in memory by design, as Sugary's estimates are.
public sealed class AskConversations(TimeProvider clock, AppsSettings settings)
{
    private sealed record Asker(string AppId, Account Viewer);

    // The agent each conversation's turns went to: the agent groups messages by conversation and agent, so
    // a follow-up names the one that holds the conversation's history.
    private readonly OpenConversations<Asker, string> _open = new(clock, settings.Asks.FollowUpWindow, Ceiling);

    // Every ask that names no conversation opens one, kept for the whole window, so the table is bounded:
    // far more conversations than a deployment's people start within a window, a few megabytes at most,
    // and past it the one longest without a turn is forgotten and its app starts afresh, as after the window.
    private const int Ceiling = 10_000;

    public string Open(string appId, Account viewer, string agentId) => _open.Open(new Asker(appId, viewer), agentId);

    // One opened for an ask that never reached the agent.
    public void Forget(string conversation) => _open.Forget(conversation);

    // Whether this app and viewer may continue the conversation on the app's building agent now: one that
    // changed since holds none of its history, so the app starts afresh rather than be answered as though
    // it did. If they may, the window starts again.
    public bool TryContinue(string appId, Account viewer, string agentId, string conversation) =>
        _open.TryContinue(new Asker(appId, viewer), conversation, out var openedOn) && openedOn == agentId;
}