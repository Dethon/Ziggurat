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