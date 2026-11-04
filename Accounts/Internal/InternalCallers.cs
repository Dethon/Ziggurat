using Accounts.Links;
using Accounts.Settings;
using Accounts.Tunnels;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Tunnel;
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

    // A household's own voice server, asking on its accounts back-call: the relay shows this service
    // the credential the tunnel was opened with, never a deployment secret, so the server is whoever
    // that live credential was minted for, and only one minted for voice is. It asks about voice alone — a tunnelled voice resolving its
    // speakers and its household's guest — and about its own household alone (Household): a stolen
    // household credential mints that household's voice people and nobody else's.
    public const string HouseholdServer = "household-server";

    // Shared servers that ask for their own view of a household's configuration and nothing else
    // (.claude/rules/household-config.md). Compose gives each an entry only once it asks.
    public const string Idealista = "mcp-idealista";
    public const string Scheduling = "mcp-scheduling";
    public const string Sugary = "mcp-sugary";
    public const string Timers = "mcp-timers";

    // Sugary's channel, which its server serves people on.
    public const string SugaryChannel = ChannelIds.Sugary;

    private const string ItemKey = "accounts.internal.callers";
    private const string HouseholdKey = "accounts.internal.household";

    private static readonly Dictionary<string, string[]> _channels = new(StringComparer.Ordinal)
    {
        [SignalR] = [ChannelIds.SignalR],
        [Telegram] = [TelegramLinks.Channel],
        [Voice] = [ChannelIds.Voice],
        [Agent] = [Tunnels],
        [HouseholdServer] = [ChannelIds.Voice],
        // Sugary asks whether the session that subscribed a phone still lives, before a reminder.
        [Sugary] = [SugaryChannel]
    };

    // Which views of a household's configuration each caller may ask for: the agent its own, each
    // shared server the one section it serves with, its secrets unsealed for it alone.
    private static readonly Dictionary<string, string[]> _views = new(StringComparer.Ordinal)
    {
        [Agent] =
        [
            HouseholdConfigProtocol.AgentView, HouseholdConfigProtocol.PeopleView, HouseholdConfigProtocol.OpenRouterKeyRefused,
            HouseholdConfigProtocol.LemonadeBoxesView
        ],
        [Idealista] = [HouseholdConfigProtocol.IdealistaView],
        [Scheduling] = [HouseholdConfigProtocol.SettingsView, HouseholdConfigProtocol.AgentNamesView],
        [Sugary] = [HouseholdConfigProtocol.SettingsView, HouseholdConfigProtocol.AgentNamesView],
        [Timers] = [HouseholdConfigProtocol.SettingsView],
        [SignalR] = [HouseholdConfigProtocol.SpacesView, HouseholdConfigProtocol.AgentNamesView],
        [Telegram] = [HouseholdConfigProtocol.TelegramBotsView, HouseholdConfigProtocol.AgentNamesView]
    };

    // The channel an assertion a caller forwards as its proof of a household must name: the one it
    // serves people on. A caller with none proves a household with call tokens alone.
    private static readonly Dictionary<string, string> _assertionChannels = new(StringComparer.Ordinal)
    {
        [SignalR] = ChannelIds.SignalR,
        [Telegram] = ChannelIds.Telegram,
        [Sugary] = SugaryChannel
    };

    // The entries whose credential this request presented; none is a caller this service does not know.
    public static IReadOnlyList<string> Presenting(HttpContext http, AccountsSettings settings)
    {
        var presented = http.Request.Headers.Authorization.ToString();
        IReadOnlyList<string> callers = [.. settings.Callers
            .Where(caller => SharedSecret.Matches(presented, caller.Value))
            .Select(caller => caller.Key)];
        http.Items[ItemKey] = callers;
        return callers;
    }

    // A caller none of the entries name may still be a household's server, by the live tunnel
    // credential it presented; it is then that server and nothing else. Only the household's voice
    // server is one: the relay lets no other server's accounts back-call through, and this does not
    // lean on that — a printer's, vault's or outpost's live credential is known and refused.
    public static async Task<HouseholdServerPresented> PresentingHouseholdServerAsync(
        HttpContext http, TunnelCredentialStore credentials, CancellationToken ct)
    {
        if (SharedSecret.Bearer(http.Request.Headers.Authorization.ToString()) is not { Length: > 0 } presented
            || await credentials.LiveAsync(presented, ct) is not { } credential)
        {
            return HouseholdServerPresented.None;
        }

        if (credential.ServerName != HouseholdServerNames.Voice)
        {
            return HouseholdServerPresented.AnotherServer;
        }

        http.Items[ItemKey] = (IReadOnlyList<string>)[HouseholdServer];
        http.Items[HouseholdKey] = credential.TenantId;
        return HouseholdServerPresented.Voice;
    }

    // The one household a household's server may ask about; null for a deployment caller, which asks
    // about any.
    public static string? Household(HttpContext http) => http.Items.TryGetValue(HouseholdKey, out var household)
        ? household as string
        : null;

    public static bool MayAskAbout(HttpContext http, string channel) =>
        http.Items.TryGetValue(ItemKey, out var item) && item is IReadOnlyList<string> callers
        && callers.Any(caller => _channels.TryGetValue(caller, out var channels) && channels.Contains(channel));

    // The callers this request presented that may read the view, in the order the settings name them;
    // none is a caller asking for another's view.
    public static IReadOnlyList<string> Reading(HttpContext http, string view) =>
        http.Items.TryGetValue(ItemKey, out var item) && item is IReadOnlyList<string> callers
            ? [.. callers.Where(caller => _views.TryGetValue(caller, out var views) && views.Contains(view))]
            : [];

    public static string? AssertionChannelOf(string caller) => _assertionChannels.GetValueOrDefault(caller);

    // The answer to a caller asking about a channel that is not its own, before anything is read.
    public static IResult NotYours => Results.StatusCode(StatusCodes.Status403Forbidden);
}

// What a presented tunnel credential is to the internal API: nobody's (401), the household's voice
// server, or another of the household's servers, which asks about nothing (403).
public enum HouseholdServerPresented
{
    None,
    Voice,
    AnotherServer
}