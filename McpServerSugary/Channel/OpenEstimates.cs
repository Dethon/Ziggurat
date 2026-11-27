using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpServerSugary.Settings;

namespace McpServerSugary.Channel;

// The conversations this server opened for a meal's first estimate, and so the only ones answers may
// continue — and only the answers of the person who opened it; somebody else's answers would carry on
// another person's meal as their own turn. A conversation stays open for FollowUpHours after its last
// turn; a restart forgets them all, and the app then estimates the meal afresh.
public sealed class OpenEstimates(TimeProvider clock, SugarySettings settings)
{
    // Far more meals than a deployment's people estimate within FollowUpHours; past it the oldest is
    // forgotten and estimated afresh (OpenConversations).
    private const int Ceiling = 10_000;

    private readonly OpenConversations<Account, ReviewCopy> _open = new(clock, TimeSpan.FromHours(settings.FollowUpHours), Ceiling);

    // The review copy the first turn asked for is kept with the conversation, because the answers
    // carry no words of the meal to name it by, and each of them asks for the same review again.
    public string Open(Account person, ReviewCopy review) => _open.Open(person, review);

    // The review copy this person's answers on this conversation ask for, or null when they may not
    // continue it; if they may, the window starts again.
    public ReviewCopy? TryContinue(Account person, string conversation) =>
        _open.TryContinue(person, conversation, out var review) ? review : null;
}