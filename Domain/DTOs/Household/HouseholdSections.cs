using System.Text.RegularExpressions;
using Domain.DTOs.WebChat;
using JetBrains.Annotations;

namespace Domain.DTOs.Household;

// The sections a household's configuration is made of, each a document of its own with a version of
// its own (.claude/rules/household-config.md). Its current shapes are the records below; a change of
// shape is a new step appended to its schema's list, never an edit of an old one.
public static partial class HouseholdSections
{
    public const string Settings = "settings";
    public const string Agents = "agents";
    public const string Keys = "keys";
    public const string Features = "features";
    public const string Spaces = "spaces";

    public static readonly SectionSchema<HouseholdSettings> SettingsSchema = new(
        Settings, [SectionSteps.Unversioned], SettingsProblems);

    public static readonly SectionSchema<AgentsSection> AgentsSchema = new(
        Agents, [SectionSteps.Unversioned], AgentsProblems);

    public static readonly SectionSchema<KeysSection> KeysSchema = new(
        Keys, [SectionSteps.Unversioned], KeysProblems,
        ["openRouter/apiKey", "idealista/apiKey", "idealista/secret", "telegramBots/*/token"]);

    public static readonly SectionSchema<FeaturesSection> FeaturesSchema = new(
        Features, [SectionSteps.Unversioned], _ => []);

    public static readonly SectionSchema<SpacesSection> SpacesSchema = new(
        Spaces, [SectionSteps.Unversioned], SpacesProblems);

    public static IReadOnlyList<SectionSchema> All { get; } =
        [SettingsSchema, AgentsSchema, KeysSchema, FeaturesSchema, SpacesSchema];

    public static SectionSchema? Named(string name) => All.FirstOrDefault(s => s.Name == name);

    // An agent's id rides in Redis keys, URLs and a Telegram chat's choice, so it is a slug.
    public static bool IsAgentId(string? id) => id is { Length: > 0 and <= 64 } && Slugs.IsValid(id);

    private static IEnumerable<string> SettingsProblems(HouseholdSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Name) || settings.Name.Length > HouseholdSettings.NameLength)
        {
            yield return $"settings.name must be 1–{HouseholdSettings.NameLength} characters";
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(settings.TimeZone, out _))
        {
            yield return $"settings.timeZone '{settings.TimeZone}' is no time zone (an IANA name such as Europe/Madrid)";
        }

        if (settings.Language is { } language && !LanguageTag().IsMatch(language))
        {
            yield return $"settings.language '{language}' is no language tag (such as es or en-GB)";
        }
    }

    private static IEnumerable<string> AgentsProblems(AgentsSection section)
    {
        foreach (var agent in section.Agents)
        {
            if (!IsAgentId(agent.Id))
            {
                yield return $"agent id '{agent.Id}' must be lowercase letters, digits and inner hyphens";
            }

            switch (agent)
            {
                case { Template: not null, Definition: not null } or { Template: null, Definition: null }:
                    yield return $"agent {agent.Id} names a template or carries a definition, exactly one";
                    break;
                case { Template: { } template } when string.IsNullOrWhiteSpace(template):
                    yield return $"agent {agent.Id} names an empty template";
                    break;
                case { Template: not null } when string.IsNullOrWhiteSpace(agent.Name):
                    yield return $"agent {agent.Id} is made from a template and needs a name";
                    break;
                case { Definition: { } definition } when definition.Id != agent.Id:
                    yield return $"agent {agent.Id} carries the definition of {definition.Id}";
                    break;
                case { Definition: not null } when agent.Name is not null || agent.CustomInstructions is not null:
                    yield return $"agent {agent.Id} carries a definition, whose own name and customInstructions are its";
                    break;
                // A bot's token is a secret, and this section is not sealed: it is the keys section's.
                case { Definition.TelegramBotToken: not null }:
                    yield return $"agent {agent.Id}'s definition carries a Telegram bot token, which belongs in the keys section";
                    break;
            }
        }

        foreach (var id in section.Agents.GroupBy(a => a.Id).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            yield return $"agent id {id} is used more than once";
        }

        if (section.DefaultAgent is { } fallback && section.Agents.All(a => a.Id != fallback))
        {
            yield return $"the default agent {fallback} is none of the household's agents";
        }
    }

    private static IEnumerable<string> KeysProblems(KeysSection keys)
    {
        if (keys.OpenRouter is { ApiKey: var openRouter } && string.IsNullOrWhiteSpace(openRouter))
        {
            yield return "keys.openRouter.apiKey is empty; leave openRouter out for no key";
        }

        if (keys.Idealista is { } idealista
            && (string.IsNullOrWhiteSpace(idealista.ApiKey) || string.IsNullOrWhiteSpace(idealista.Secret)))
        {
            yield return "keys.idealista needs both apiKey and secret; leave idealista out for none";
        }

        foreach (var (agentId, bot) in keys.TelegramBots)
        {
            if (!IsAgentId(agentId))
            {
                yield return $"Telegram bot for '{agentId}': the key is the id of the agent it is bound to";
            }

            if (string.IsNullOrWhiteSpace(bot.Token))
            {
                yield return $"Telegram bot for {agentId} has an empty token";
            }
        }
    }

    private static IEnumerable<string> SpacesProblems(SpacesSection section)
    {
        if (section.Spaces.Count == 0)
        {
            yield return "a household has at least one space";
        }

        foreach (var space in section.Spaces)
        {
            if (!SpaceConfig.IsValidSlug(space.Slug))
            {
                yield return $"space slug '{space.Slug}' must be lowercase letters, digits and inner hyphens";
            }

            if (string.IsNullOrWhiteSpace(space.Name))
            {
                yield return $"space {space.Slug} needs a name";
            }

            if (!SpaceConfig.IsValidHexColor(space.AccentColor))
            {
                yield return $"space {space.Slug}'s accentColor '{space.AccentColor}' is no hex colour (#rgb, #rrggbb or #rrggbbaa)";
            }

            if (space.DefaultAgent is { } agent && !IsAgentId(agent))
            {
                yield return $"space {space.Slug}'s default agent '{agent}' is no agent id";
            }
        }

        foreach (var slug in section.Spaces.GroupBy(s => s.Slug).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            yield return $"space slug {slug} is used more than once";
        }
    }

    [GeneratedRegex("^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTag();
}

// The household's own name, the zone its dates and schedules are read in, and the language its
// agents answer in unless a conversation says otherwise (none: they follow the conversation).
[PublicAPI]
public sealed record HouseholdSettings
{
    public const int NameLength = 200;

    public string Name { get; init; } = "";
    public string TimeZone { get; init; } = "";
    public string? Language { get; init; }
}

// The household's agents and the one a channel that names none reaches (the shared Telegram bot's
// default; none leaves the choice to the agent's own default).
[PublicAPI]
public sealed record AgentsSection
{
    public string? DefaultAgent { get; init; }
    public IReadOnlyList<HouseholdAgent> Agents { get; init; } = [];
}

// One of the household's agents: either a shipped template with the name and custom instructions the
// household gave it, or — the home household's own agents — a full definition seeded from what the
// deployment ran before, in the shape of today's AgentDefinition, which carries its own name and
// instructions (and never a bot token: bots are the keys section's).
[PublicAPI]
public sealed record HouseholdAgent
{
    public string Id { get; init; } = "";
    public string? Template { get; init; }
    public string? Name { get; init; }
    public string? CustomInstructions { get; init; }
    public AgentDefinition? Definition { get; init; }
}

// The household's secrets. Every field is sealed at rest, and each is unsealed only into the view of
// the server it belongs to: the OpenRouter key into the agent's, Idealista's into Idealista's, the
// bots into Telegram's. A bot is keyed by the id of the agent it is bound to, which is also what its
// token is sealed for, so one bot's token moved under another's entry does not open.
[PublicAPI]
public sealed record KeysSection
{
    public OpenRouterKey? OpenRouter { get; init; }
    public IdealistaKeys? Idealista { get; init; }
    public IReadOnlyDictionary<string, TelegramBotKey> TelegramBots { get; init; } = new Dictionary<string, TelegramBotKey>();
}

[PublicAPI]
public sealed record OpenRouterKey(string ApiKey);

[PublicAPI]
public sealed record IdealistaKeys(string ApiKey, string Secret);

[PublicAPI]
public sealed record TelegramBotKey(string Token);

// Which shared features the household's agents are offered. A household that saved none has these:
// everything but Idealista, which searches on the household's own key and does nothing without one.
[PublicAPI]
public sealed record FeaturesSection
{
    public bool Scheduling { get; init; } = true;
    public bool Timers { get; init; } = true;
    public bool Sugary { get; init; } = true;
    public bool Idealista { get; init; }
    public bool Memory { get; init; } = true;
    public bool Workers { get; init; } = true;
}

// The household's chat-client spaces. A household that saved none has the one default space.
[PublicAPI]
public sealed record SpacesSection
{
    public IReadOnlyList<HouseholdSpace> Spaces { get; init; } = [HouseholdSpace.Default];
}

[PublicAPI]
public sealed record HouseholdSpace
{
    public static readonly HouseholdSpace Default = new()
    {
        Slug = SpaceConfig.DefaultSlug, Name = "Main", AccentColor = SpaceConfig.DefaultAccentColor
    };

    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string AccentColor { get; init; } = SpaceConfig.DefaultAccentColor;

    // The agent a conversation opened in this space starts on unless the person picked another.
    public string? DefaultAgent { get; init; }

    // Where the space sits in the client's list, lowest first.
    public int Order { get; init; }
}