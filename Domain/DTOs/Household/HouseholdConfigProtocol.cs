using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs.Household;

// What the accounts service and the readers of a household's configuration say to each other, spelled
// once here so both ends read the same records (.claude/rules/household-config.md).
public static class HouseholdConfigProtocol
{
    // Under the accounts service's internal prefix, which the proxy answers 404 on every host. One
    // route per view; each caller may ask for its own views alone.
    public const string PathPrefix = "/accounts/internal/household-config/";

    // The agent's: the household's settings, agents, features and spaces, and its OpenRouter key.
    public const string AgentView = "agent";

    // Every shared server that reads dates in the household's zone: scheduling, Sugary.
    public const string SettingsView = "settings";

    // Idealista's own keys.
    public const string IdealistaView = "idealista";

    // The chat hub's: the household's spaces.
    public const string SpacesView = "spaces";

    // The Telegram server's: every household's bots at once, since it polls them from startup,
    // before any call names a household.
    public const string TelegramBotsView = "telegram-bots";

    // Where a save is announced (a HouseholdConfigSaved, as JSON), so every reader drops what it
    // cached of that household. Pub/sub reaches only who is subscribed then: a reader's cache
    // lifetime is what bounds a missed one.
    public const string SavedChannel = "accounts:household-config:saved";

    // The channel the agent's own token for the configuration names: it acts for nobody, and names
    // its household in the query (the agent is the one caller that may).
    public const string AgentTokenChannel = "household-config";

    public static string PathOf(string view) => PathPrefix + view;
}

// A question for one view. A shared server proves which household it asks about with what it was
// handed for that household — the agent's call token for it, the accounts service's assertion on
// its own channel, or (the scheduling server, for a fire) the delegation stored with the schedule —
// and never names one. The agent names the household (HouseholdId) and presents a
// token of its own signed for the configuration (TokenAudiences.HouseholdConfiguration), since it is
// the party that signs every other proof. The Telegram server's all-households view asks with none.
[PublicAPI]
public sealed record HouseholdConfigQuery(
    string? HouseholdId = null, string? CallToken = null, string? Assertion = null, string? Delegation = null);

// The agent's view of one household.
[PublicAPI]
public sealed record AgentHouseholdConfig(
    string HouseholdId,
    bool IsHome,
    HouseholdSettings Settings,
    AgentsSection Agents,
    FeaturesSection Features,
    SpacesSection Spaces,
    string? OpenRouterApiKey);

// A shared server's view of the household its proof named: which household that is, and its section.
[PublicAPI]
public sealed record HouseholdConfigAnswer<T>(string HouseholdId, T Section);

// The Telegram server's view: every bot of every household, each bound to one of its agents.
[PublicAPI]
public sealed record HouseholdTelegramBots(IReadOnlyList<HouseholdTelegramBot> Bots);

[PublicAPI]
public sealed record HouseholdTelegramBot(string HouseholdId, string AgentId, string Token);

// Why a question was not answered, with what the service could say about it.
[PublicAPI]
public sealed record HouseholdConfigRefused(HouseholdConfigRefusal Reason, string Detail);

[JsonConverter(typeof(JsonStringEnumConverter<HouseholdConfigRefusal>))]
public enum HouseholdConfigRefusal
{
    // The query was not one this view takes: no proof, two, or a household named by a caller that
    // may not name one.
    BadQuery,

    // The proof did not verify, was for another server or channel, or named nobody (Detail says
    // which).
    Unproven,

    // The proof named a household this service does not keep.
    UnknownHousehold,

    // The household's secrets cannot be opened here: no master key, or a data key wrapped under
    // another one.
    SecretsUnavailable
}

// Published on HouseholdConfigProtocol.SavedChannel when a section is saved.
[PublicAPI]
public sealed record HouseholdConfigSaved(string HouseholdId, string Section);