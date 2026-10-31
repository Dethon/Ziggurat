using Accounts.Links;
using Accounts.Settings;
using Accounts.Tunnels;
using Domain.DTOs.Channel;
using Domain.Security;

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

    // The agent, whose relay verifies the credential every tunnel opens with. It asks about no
    // person's channel: tunnel credentials are what it may ask about, and nothing else may.
    public const string Agent = "agent";

    // Not a channel: what the relay asks about, kept in the same table so a channel server's
    // credential is refused it as it is refused another channel.
    public const string Tunnels = "tunnels";

    // A household's own server, asking on its accounts back-call: the relay shows this service the
    // credential the tunnel was opened with, never a deployment secret, so the server is whoever that
    // live credential was minted for. It asks about voice alone — a tunnelled voice resolving its
    // speakers and its household's guest — and about its own household alone (Household): a stolen
    // household credential mints that household's voice people and nobody else's.
    public const string HouseholdServer = "household-server";

    private const string ItemKey = "accounts.internal.callers";
    private const string HouseholdKey = "accounts.internal.household";

    private static readonly Dictionary<string, string[]> _channels = new(StringComparer.Ordinal)
    {
        [SignalR] = [ChannelIds.SignalR],
        [Telegram] = [TelegramLinks.Channel],
        [Voice] = [ChannelIds.Voice],
        [Agent] = [Tunnels],
        [HouseholdServer] = [ChannelIds.Voice]
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

    // A caller none of the entries name may still be a household's server, by the live tunnel
    // credential it presented; it is then that server and nothing else.
    public static async Task<bool> PresentingHouseholdServerAsync(
        HttpContext http, TunnelCredentialStore credentials, CancellationToken ct)
    {
        if (SharedSecret.Bearer(http.Request.Headers.Authorization.ToString()) is not { Length: > 0 } presented
            || await credentials.LiveAsync(presented, ct) is not { } credential)
        {
            return false;
        }

        http.Items[ItemKey] = (IReadOnlyList<string>)[HouseholdServer];
        http.Items[HouseholdKey] = credential.TenantId;
        return true;
    }

    // The one household a household's server may ask about; null for a deployment caller, which asks
    // about any.
    public static string? Household(HttpContext http) => http.Items.TryGetValue(HouseholdKey, out var household)
        ? household as string
        : null;

    public static bool MayAskAbout(HttpContext http, string channel) =>
        http.Items.TryGetValue(ItemKey, out var item) && item is IReadOnlyList<string> callers
        && callers.Any(caller => _channels.TryGetValue(caller, out var channels) && channels.Contains(channel));

    // The answer to a caller asking about a channel that is not its own, before anything is read.
    public static IResult NotYours => Results.StatusCode(StatusCodes.Status403Forbidden);
}