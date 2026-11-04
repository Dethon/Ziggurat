using System.Text;
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

    // Every household's own Lemonade box, the agent's alone: it keeps each one's models in its
    // household's picker, so it asks for all of them at once, on its own token.
    public const string LemonadeBoxesView = "lemonade-boxes";

    // The ids and names of a household's agents and which answers by default: what the shared Telegram
    // bot's /agent lists and switches a chat among, and nothing else of their definitions.
    public const string AgentNamesView = "agent-names";

    // The agent's, for work no call is behind (dreaming): every household and the people in it, each
    // then read on its own view for its key. Asked naming no household.
    public const string PeopleView = "people";

    // What a change of a household's people — a member disabled or re-enabled — is announced as on
    // SavedChannel, so the agent drops the view that says who is disabled. No section is called this:
    // a reader drops all it kept of the household whatever section a save names.
    public const string PeopleChanged = "people";

    // Not a view: the agent saying OpenRouter refused a household's own key in a turn, asked as its
    // own view is (the household named, the agent's token beside its credential) and answered 204.
    public const string OpenRouterKeyRefused = "openrouter-key-refused";

    // Where a save is announced (a HouseholdConfigSaved, as JSON), so every reader drops what it
    // cached of that household. Pub/sub reaches only who is subscribed then: a reader's cache
    // lifetime is what bounds a missed one.
    public const string SavedChannel = "accounts:household-config:saved";

    // What a save announcement names when the household was suspended or resumed rather than a section
    // saved: no section is called this, and every reader drops what it kept of the household either way.
    public const string SuspensionChanged = "suspension";

    // What a tunnel credential minted or revoked is announced as on SavedChannel, so the agent drops
    // the view that names the own servers a household set up (AgentHouseholdConfig.OwnServers). No
    // section is called this either.
    public const string OwnServersChanged = "own-servers";

    // The channel the agent's own token for the configuration names: it acts for nobody, and names
    // its household in the query (the agent is the one caller that may).
    public const string AgentTokenChannel = "household-config";

    // What the agent names the home household by in its view's query, for what is the home's before
    // any turn names its id: the catalogue it registers with its channels, and a turn that acts for
    // nobody. A household id is 32 lowercase hex characters, so no household is ever called this; the
    // answer carries the home's real id.
    public const string HomeHousehold = "@home";

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

// Many sections at once, with the model key unsealed, where a shared server is handed its own section
// alone: the agent builds the household's agents from them and pays for their models. DisabledPeople
// are the user ids of the members an owner disabled: the agent refuses their turns and believes no
// delegation naming them, so nothing it does acts for them until they are re-enabled. Suspended is the
// operator's word that the household runs nothing: the agent refuses its turns and fires by it.
// OwnServers are the server names of its own servers the household set up — those it holds a live
// tunnel credential for, a machine's never among them — whether or not one is connected now; the
// agent's household withholds every other own server its agents name. None (an accounts service
// from before it) is none set up.
[PublicAPI]
public sealed record AgentHouseholdConfig(
    string HouseholdId,
    bool IsHome,
    HouseholdSettings Settings,
    AgentsSection Agents,
    FeaturesSection Features,
    SpacesSection Spaces,
    string? OpenRouterApiKey,
    IReadOnlyList<string>? DisabledPeople = null,
    bool Suspended = false,
    IReadOnlyList<string>? OwnServers = null,
    LemonadeBox? Lemonade = null)
{
    // The key goes over the wire in clear, as the agent needs it; printed — a log line, an assertion's
    // message, the HouseholdAgents built from it — it says only whether there is one.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"HouseholdId = {HouseholdId}, IsHome = {IsHome}, Settings = {Settings}, Agents = {Agents}, ");
        builder.Append($"Features = {Features}, Spaces = {Spaces}, OpenRouterApiKey = {(OpenRouterApiKey is null ? "none" : "[set]")}, ");
        builder.Append($"DisabledPeople = [{string.Join(", ", DisabledPeople ?? [])}], Suspended = {Suspended}, ");
        builder.Append($"OwnServers = [{string.Join(", ", OwnServers ?? [])}], Lemonade = {Lemonade?.ToString() ?? "none"}");
        return true;
    }
}

// A shared server's view of the household its proof named: which household that is, and its section.
[PublicAPI]
public sealed record HouseholdConfigAnswer<T>(string HouseholdId, T Section);

// The Telegram server's view: every bot of every household, each bound to one of its agents.
[PublicAPI]
public sealed record HouseholdTelegramBots(IReadOnlyList<HouseholdTelegramBot> Bots);

[PublicAPI]
public sealed record HouseholdTelegramBot(string HouseholdId, string AgentId, string Token);

// The agent's view of every household's own Lemonade box.
[PublicAPI]
public sealed record HouseholdLemonadeBoxes(IReadOnlyList<HouseholdLemonadeBox> Boxes);

[PublicAPI]
public sealed record HouseholdLemonadeBox(string HouseholdId, LemonadeBox Box);

// A household's agents as a person picks among them: each one's id and name, in the section's order,
// and the one answering where nobody picked (none: the agent's own default decides).
[PublicAPI]
public sealed record HouseholdAgentNames(string? DefaultAgent, IReadOnlyList<HouseholdAgentName> Agents);

// What a picker shows of an agent beside its name — its description, and the model and effort it runs
// on, which only the household's configuration knows for an agent the deployment does not run itself.
// Never its custom instructions.
[PublicAPI]
public sealed record HouseholdAgentName(
    string Id,
    string Name,
    string? Description = null,
    string? Model = null,
    string? ReasoningEffort = null);

// The agent's view across households: each household, and the user ids of its people (none for a
// household nobody has joined yet).
[PublicAPI]
public sealed record HouseholdPeople(IReadOnlyList<HouseholdMembers> Households);

[PublicAPI]
public sealed record HouseholdMembers(string HouseholdId, IReadOnlyList<string> People);

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

// Published on HouseholdConfigProtocol.SavedChannel when a section is saved, and when the household is
// suspended or resumed (HouseholdConfigProtocol.SuspensionChanged), which its readers must see as soon.
[PublicAPI]
public sealed record HouseholdConfigSaved(string HouseholdId, string Section);