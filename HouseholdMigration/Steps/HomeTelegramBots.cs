using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Household;

namespace HouseholdMigration.Steps;

// The home's jack and jonas bots were two tokens in the deployment's .env (BOTS__0 jack's, BOTS__1
// jonas's), polled by the Telegram channel beside nothing else; a household's bots are its keys
// section's now (.claude/rules/household-config.md), each keyed by the agent it is bound to, and the
// deployment keeps one token of its own, the shared bot's. So the home is given its two bots, sealed
// like any secret, through the accounts service's seed route — which leaves a keys section the
// household already saved as it is: a rerun changes nothing, and a section the operator set since is
// theirs, to which they add the bots with set-household-config.
//
// The tokens are the run's input (--telegram-bots, a file or `-` for what is piped in), never part of
// the command line or a file in the volume, as every secret this repo hands a command.
public sealed class HomeTelegramBots(HouseholdConfigSeed seed, JsonObject? bots) : IMigrationStep
{
    public const string Argument = "--telegram-bots";

    // The bots' file as the keys section spells them, `{ "<agent id>": { "token": "..." } }`, checked
    // before anything is touched: each one bound to one of the home's agents, each with a token. The
    // problems never quote a value, which may be a token.
    public static (JsonObject? Bots, IReadOnlyList<string> Problems) Read(string text)
    {
        JsonObject? bots;
        try
        {
            bots = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            bots = null;
        }

        if (bots is not { Count: > 0 })
        {
            return (null, [$"{Argument}: the input is not a JSON object of bots, {{ \"<agent id>\": {{ \"token\": \"...\" }} }}"]);
        }

        var homeAgents = HouseholdSections.AgentsSchema.Read(HouseholdSections.AgentsSchema.Migrate(HomeConfiguration.Agents))
            .Agents.Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> problems = [.. bots.Select(bot => Problem(bot.Key, bot.Value, homeAgents)).OfType<string>()];
        return problems.Count > 0 ? (null, problems) : (bots, []);
    }

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("The home's own Telegram bots into its keys");
        if (bots is null)
        {
            report.Lines.Add($"no bots given ({Argument} <file|->): the home's jack and jonas bots are not seeded");
            return report;
        }

        var agents = string.Join(", ", bots.Select(bot => bot.Key));
        switch (await seed.SeedAsync(household.TenantId, HouseholdSections.Keys,
                    new JsonObject { ["telegramBots"] = bots.DeepClone() }, dryRun, ct))
        {
            case Seeding.Refused refused:
                report.Problems.Add($"the keys: {refused.Why}");
                break;
            case Seeding.AlreadySaved:
                report.Lines.Add("the household already saved its keys; they are left as they are "
                                 + "(add the bots there with set-household-config --section keys)");
                break;
            default:
                report.Changes++;
                report.Lines.Add($"telegramBots: {agents}, each bound to its agent and sealed");
                break;
        }

        return report;
    }

    private static string? Problem(string agent, JsonNode? bot, IReadOnlySet<string> homeAgents) =>
        !homeAgents.Contains(agent)
            ? $"{Argument}: a bot for '{Printable(agent)}', which is none of the home's agents ({string.Join(", ", homeAgents)})"
            : bot is JsonObject { Count: 1 } entry && entry["token"] is JsonValue token
              && token.TryGetValue<string>(out var value) && value.Trim().Length > 0
                ? null
                : $"{Argument}: the bot for {agent} is not {{ \"token\": \"...\" }} with a token";

    // An agent id as somebody typed it, shortened and stripped of anything that is not plainly a name,
    // since a token pasted in the wrong place must not be printed back.
    private static string Printable(string agent) =>
        agent.Length <= 32 && agent.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? agent : "(not an agent id)";
}