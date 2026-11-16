using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Household;

namespace HouseholdMigration.Steps;

// The home's own keys were the deployment's .env: its jack and jonas bots two tokens (BOTS__0 jack's,
// BOTS__1 jonas's) beside nothing else, and Idealista's key and secret the Idealista server's
// (IDEALISTA__APIKEY, IDEALISTA__SECRET). A household's keys are its keys section's now
// (.claude/rules/household-config.md) — a bot keyed by the agent it is bound to — and the deployment
// keeps one token of its own, the shared bot's, and no Idealista key at all. So the home is given its
// keys, sealed like any secret, through the accounts service's seed route, which gives a keys section
// the household already saved only the keys it lacks: one the operator set by hand before the run is
// kept, the rest are added beside it, and a rerun has nothing left to give.
//
// The keys are the run's input (--home-keys, a file or `-` for what is piped in), never part of the
// command line or a file in the volume, as every secret this repo hands a command. Not the OpenRouter
// key: the home's turns are paid with the deployment's.
public sealed class HomeKeys(HouseholdConfigSeed seed, JsonObject? keys) : IMigrationStep
{
    public const string Argument = "--home-keys";

    private const string Bots = "telegramBots";
    private const string Idealista = "idealista";

    public const string Shape =
        "{ \"telegramBots\": { \"<agent id>\": { \"token\": \"...\" } }, \"idealista\": { \"apiKey\": \"...\", \"secret\": \"...\" } }";

    // The keys' file as the keys section spells them, checked before anything is touched: each bot
    // bound to one of the home's agents with a token, Idealista's as the pair it is, and nothing else.
    // The problems never quote a value, which may be a key.
    public static (JsonObject? Keys, IReadOnlyList<string> Problems) Read(string text)
    {
        JsonObject? keys;
        try
        {
            keys = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            keys = null;
        }

        if (keys is null || !keys.Any(field => field.Key is Bots or Idealista))
        {
            return (null, [$"{Argument}: the input is not the home's keys, {Shape}, either one or both"]);
        }

        var homeAgents = HouseholdSections.AgentsSchema.Read(HouseholdSections.AgentsSchema.Migrate(HomeConfiguration.Agents))
            .Agents.Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> problems = [.. keys.SelectMany(field => Problems(field.Key, field.Value, homeAgents))];
        return problems.Count > 0 ? (null, problems) : (keys, []);
    }

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("The home's own keys: its Telegram bots and Idealista's");
        if (keys is null)
        {
            report.Lines.Add($"no keys given ({Argument} <file|->): the home's jack and jonas bots and Idealista's keys are not seeded");
            return report;
        }

        switch (await seed.SeedAsync(household.TenantId, HouseholdSections.Keys, (JsonObject)keys.DeepClone(), dryRun, ct))
        {
            case Seeding.Refused refused:
                report.Problems.Add($"the keys: {refused.Why}");
                break;
            case Seeding.AlreadySaved:
                report.Lines.Add("the household already holds every one of these keys; each is kept as it is");
                break;
            case Seeding.Seeded seeded:
                report.Changes++;
                report.Lines.AddRange(Given(seeded.Kept));
                break;
        }

        return report;
    }

    // What the seed gave, and what it kept of the household's own.
    private IEnumerable<string> Given(IReadOnlyList<string> kept)
    {
        var bots = (keys![Bots] as JsonObject ?? []).Select(bot => bot.Key).ToList();
        var given = bots.Where(bot => !kept.Contains($"{Bots}/{bot}")).ToList();
        var held = bots.Except(given).ToList();
        if (given.Count > 0)
        {
            yield return $"{Bots}: {string.Join(", ", given)}, each bound to its agent and sealed";
        }

        if (held.Count > 0)
        {
            yield return $"{Bots}: {string.Join(", ", held)}, which the household already held, kept as they are";
        }

        if (keys[Idealista] is not null)
        {
            yield return kept.Contains(Idealista)
                ? "Idealista's keys the household already held are kept as they are"
                : "Idealista's key and secret, sealed";
        }
    }

    private static IEnumerable<string> Problems(string field, JsonNode? value, IReadOnlySet<string> homeAgents) => field switch
    {
        Bots when value is JsonObject { Count: > 0 } bots => bots.Select(bot => BotProblem(bot.Key, bot.Value, homeAgents)).OfType<string>(),
        Bots => [$"{Argument}: telegramBots is not {{ \"<agent id>\": {{ \"token\": \"...\" }} }}"],
        Idealista when value is JsonObject { Count: 2 } pair && Filled(pair["apiKey"]) && Filled(pair["secret"]) => [],
        Idealista => [$"{Argument}: idealista is not {{ \"apiKey\": \"...\", \"secret\": \"...\" }}, both of the pair"],
        "openRouter" => [$"{Argument}: an OpenRouter key, which the home has none of its own: its turns are paid with the deployment's"],
        _ => [$"{Argument}: '{Printable(field)}' is none of the home's keys ({Bots}, {Idealista})"]
    };

    private static string? BotProblem(string agent, JsonNode? bot, IReadOnlySet<string> homeAgents) =>
        !homeAgents.Contains(agent)
            ? $"{Argument}: a bot for '{Printable(agent)}', which is none of the home's agents ({string.Join(", ", homeAgents)})"
            : bot is JsonObject { Count: 1 } entry && Filled(entry["token"])
                ? null
                : $"{Argument}: the bot for {agent} is not {{ \"token\": \"...\" }} with a token";

    private static bool Filled(JsonNode? value) =>
        value is JsonValue text && text.TryGetValue<string>(out var s) && s.Trim().Length > 0;

    // A name as somebody typed it, shortened and stripped of anything that is not plainly a name,
    // since a key pasted in the wrong place must not be printed back.
    private static string Printable(string name) =>
        name.Length <= 32 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? name : "(not a name)";
}