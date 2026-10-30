using Accounts.Links;
using Accounts.Settings;
using Domain.DTOs.Channel;

namespace Accounts.Internal;

// Who is asking the internal API, and what each may ask about: a caller is the CALLERS__<host> entry
// whose credential it presented, and each host asks about its own channel alone — the chat hub
// refreshes a session's person on `signalr`, the Telegram bot resolves Telegram ids and redeems its
// ways in, the voice channel resolves voice profiles and the household's guest. So the credential a
// channel server already holds for its own /mcp, if it leaks, mints that channel's people and no
// other's. A host named here with no channel (the household migration, which has an endpoint of its
// own) or not named at all asks about nothing.
//
// A credential several entries share — a host run hands every caller the one local secret — is each
// of those callers at once, and may ask what any of them may.
public static class InternalCallers
{
    public const string SignalR = "mcp-channel-signalr";
    public const string Telegram = "mcp-channel-telegram";
    public const string Voice = "mcp-channel-voice";

    private const string ItemKey = "accounts.internal.callers";

    private static readonly Dictionary<string, string[]> _channels = new(StringComparer.Ordinal)
    {
        [SignalR] = [ChannelIds.SignalR],
        [Telegram] = [TelegramLinks.Channel],
        [Voice] = [ChannelIds.Voice]
    };

    // The entries whose credential this request presented; none is a caller this service does not know.
    public static IReadOnlyList<string> Presenting(HttpContext http, AccountsSettings settings)
    {
        var presented = http.Request.Headers.Authorization.ToString();
        IReadOnlyList<string> callers = [.. settings.Callers
            .Where(caller => CallerCredential.Matches(presented, caller.Value))
            .Select(caller => caller.Key)];
        http.Items[ItemKey] = callers;
        return callers;
    }

    public static bool MayAskAbout(HttpContext http, string channel) =>
        http.Items.TryGetValue(ItemKey, out var item) && item is IReadOnlyList<string> callers
        && callers.Any(caller => _channels.TryGetValue(caller, out var channels) && channels.Contains(channel));

    // The answer to a caller asking about a channel that is not its own, before anything is read.
    public static IResult NotYours => Results.StatusCode(StatusCodes.Status403Forbidden);
}