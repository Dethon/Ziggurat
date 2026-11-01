using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs.Admin;

// The admin API on the accounts service (.claude/rules/admin.md), spelled once for the service and
// for the two browser clients that call it: the owner's admin area in the chat client and the
// operator's household list in the dashboard. Every route sits under one of two roots, which decide
// who may call it: /household acts on the household the caller's assertion names and is its owners',
// /operator acts across households and is the operator's.
public static class AdminProtocol
{
    public const string Root = "/accounts/admin";
    public const string Household = Root + "/household";
    public const string Operator = Root + "/operator";

    public const string Settings = Household + "/settings";
    public const string HouseholdChanges = Household + "/changes";
    public const string AllChanges = Operator + "/changes";

    // The household's keys: read as their states alone, and each saved on its own route once its
    // provider has been asked about it. A bot is put under the id of the agent it is bound to.
    public const string Keys = Household + "/keys";
    public const string OpenRouterKey = Keys + "/openrouter";
    public const string IdealistaKeys = Keys + "/idealista";
    public const string TelegramBots = Keys + "/telegram";

    public static string TelegramBot(string agentId) => $"{TelegramBots}/{Uri.EscapeDataString(agentId)}";

    // How many of a saved key's last characters an answer shows, so the owner can tell which key is
    // saved without it being shown; a key too short to spare them shows none.
    public const int KeyCharactersShown = 4;
    public const int ShortestKeyShown = 12;
    // The operator's household list: GET lists every household, POST creates one with its owner.
    public const string Households = Operator + "/households";

    // A recovery link for one owner of one household, minted by the operator.
    public const string OwnerRecoveryRoute = Households + "/{householdId}/owners/{userId}/recovery";

    public static string OwnerRecovery(string householdId, string userId) =>
        $"{Households}/{Uri.EscapeDataString(householdId)}/owners/{Uri.EscapeDataString(userId)}/recovery";

    // Whether a household runs: PUT a HouseholdSuspension to suspend it or resume it.
    public const string SuspensionRoute = Households + "/{householdId}/suspension";

    public static string Suspension(string householdId) => $"{Households}/{Uri.EscapeDataString(householdId)}/suspension";

    // The household's agents (AdminAgents): each one's name and custom instructions, its last few
    // versions and a restore of one, an agent added from a shipped template, one removed.
    public const string Agents = Household + "/agents";

    public static string Agent(string agentId) => $"{Agents}/{agentId}";

    public static string AgentVersions(string agentId) => $"{Agent(agentId)}/versions";

    public static string RestoreAgentVersion(string agentId, string versionId) => $"{AgentVersions(agentId)}/{versionId}/restore";

    // The household's people (AdminMembers): an invite, a member's role and sees-all, disabling and
    // re-enabling one, and a recovery link for one.
    public const string Members = Household + "/members";
    public const string MemberInvites = Members + "/invites";

    public static string Member(string userId) => $"{Members}/{userId}";

    public static string DisableMember(string userId) => $"{Member(userId)}/disable";

    public static string EnableMember(string userId) => $"{Member(userId)}/enable";

    public static string MemberRecovery(string userId) => $"{Member(userId)}/recovery";

    // The household's features (the features section, whole) and its chat-client spaces (AdminSpaces
    // read, the spaces section saved whole).
    public const string Features = Household + "/features";
    public const string Spaces = Household + "/spaces";

    // The household's own servers (AdminServers): a tunnel credential minted for a server by name, and
    // one revoked, which closes the tunnel it opened.
    public const string Servers = Household + "/servers";

    public static string RevokeServerCredential(string credentialId) =>
        $"{Servers}/credentials/{Uri.EscapeDataString(credentialId)}/revoke";

    // The household's usage over a month (AdminUsage), ?month=yyyy-MM, the current one when none is named.
    public const string Usage = Household + "/usage";

    // Every admin request carries this header with this value. A cross-site form cannot set a
    // header, and a cross-origin script that does is preflighted, which nothing here answers: so a
    // request carrying it came from a page on the host the API is served on.
    public const string RequestHeader = "X-Requested-With";
    public const string RequestHeaderValue = "ziggurat-admin";

    // How many changes a read answers when it names no limit, and the most it ever answers.
    public const int DefaultChangeCount = 50;
    public const int MaxChangeCount = 200;
}

// What a refused change says: a sentence for the person and, for a value the section's rules
// refused, each problem as the rules phrase it. Never a value that was sent.
[PublicAPI]
public sealed record AdminRefusal(string Message, IReadOnlyList<string> Problems);

// A change that stood, with what is now saved, and a warning when it stood with a caveat (its
// readers not told at once, a key the provider could not be asked about).
[PublicAPI]
public sealed record AdminSaved<T>(T Value, string? Warning = null);

// One recorded change: who made it (their account, and the name they go by now), what (the route it
// came on and a summary that never carries a secret), on which household, and when.
[PublicAPI]
public sealed record AdminChangeEntry(
    string Id,
    string HouseholdId,
    string ActorUserId,
    string ActorTenantId,
    string ActorName,
    string Route,
    string Summary,
    DateTimeOffset At);
// What the household's keys are, never what they say: each saved key's state, when it was last
// checked and its last few characters. The home household's agents run on the deployment's key
// whatever its own section holds, so only another household needs an OpenRouter key of its own.
[PublicAPI]
public sealed record HouseholdKeys(
    bool PaysWithDeploymentKey,
    KeyStatus? OpenRouter,
    KeyStatus? Idealista,
    IReadOnlyList<TelegramBotStatus> TelegramBots)
{
    // Its agents refuse every turn until one is set: what the owner is sent to the keys page for.
    public bool NeedsOpenRouterKey => !PaysWithDeploymentKey && OpenRouter is null;
}

// Working: the provider accepted it when last asked. Rejected: the provider refused it, on saving
// or in use. Unchecked: saved while the provider could not be asked, or saved by the operator's
// command, which asks nobody.
[JsonConverter(typeof(JsonStringEnumConverter<KeyState>))]
public enum KeyState
{
    Working,
    Rejected,
    Unchecked
}

[PublicAPI]
public sealed record KeyStatus(KeyState State, string LastCharacters, DateTimeOffset? CheckedAt, string? Reason = null);

[PublicAPI]
public sealed record TelegramBotStatus(string AgentId, KeyStatus Key);
// A household the operator creates: its name and the display name of the owner it starts with.
[PublicAPI]
public sealed record NewHousehold(string Name, string OwnerName);

// A link that enrolls a passkey for one owner of a household — the owner a new household starts
// with, or one the operator recovers. Shown once, in the answer that minted it: only its hash is kept.
[PublicAPI]
public sealed record OwnerLink(string HouseholdId, string UserId, string DisplayName, string Url, DateTimeOffset ExpiresAt);

// One household as the operator's list shows it: its people by role, the servers connected to the
// agent's relay now (null when the agent could not be asked), when any of its people's sessions was
// last seen by the session check (null when none ever was), and since when it is suspended (null
// while it runs).
[PublicAPI]
public sealed record HouseholdListEntry(
    string Id,
    string Name,
    bool IsHome,
    DateTimeOffset CreatedAt,
    IReadOnlyList<HouseholdPerson> Owners,
    IReadOnlyList<HouseholdPerson> Members,
    IReadOnlyList<HouseholdConnectedServer>? ConnectedServers,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? SuspendedAt = null);

[PublicAPI]
public sealed record HouseholdPerson(string UserId, string DisplayName, bool IsOperator);

[PublicAPI]
public sealed record HouseholdConnectedServer(string Name, DateTimeOffset ConnectedSince);
// A household suspended or resumed by the operator: sent with Suspended alone, answered with since when
// it is suspended (null once resumed). While suspended its sessions, turns, fires and tunnels are all
// refused, and nothing of it is deleted.
[PublicAPI]
public sealed record HouseholdSuspension(bool Suspended, DateTimeOffset? Since = null);