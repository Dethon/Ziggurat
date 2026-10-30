using HouseholdMigration.Steps;
using Infrastructure.Memory;
using Infrastructure.StateManagers;
using StackExchange.Redis;

namespace HouseholdMigration;

// Carries today's people and what is filed under their names into the home household, once, on
// deploy (.scratch/accounts/spec.md, "Migration"), run from its compose service:
//
//   docker compose --profile migrate run --rm household-migration \
//     --people /input/people.json --operator <name> --owner <name> --sugary <name> [--dry-run]
//
// Every step finds what is still under an old name and moves it, so a second run finds nothing and
// says so, and a run that stopped half way is finished by running it again. --dry-run says what it
// would do and writes nothing anywhere, the accounts service included.
public static class HouseholdMigrationCommand
{
    private const string Usage =
        "usage: --people <file> --operator <name> --owner <name> --sugary <name> [--dry-run]\n"
        + "  each <name> is a person's \"name\" in the file: the identity map's Name";

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, MigrationSettings settings, TextWriter output, CancellationToken ct)
    {
        if (Arguments.Parse(args) is not { } asked)
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        PeopleFile file;
        try
        {
            file = PeopleFile.Read(asked.People);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await output.WriteLineAsync($"nothing was migrated: {asked.People} could not be read: {ex.Message}");
            return 2;
        }

        var problems = file.Problems(asked.Operator, asked.Owner, asked.Sugary).ToList();
        if (settings.Accounts.Credential.Length == 0)
        {
            problems.Add("ACCOUNTS__CREDENTIAL is not set (HOUSEHOLD_MIGRATION_SECRET in .env)");
        }

        byte[]? stateKey = null;
        try
        {
            stateKey = RedisPersonStateStore.ParseKey(settings.Logins.StateKey, "LOGINS__STATEKEY");
        }
        catch (InvalidOperationException ex)
        {
            problems.Add(ex.Message);
        }

        if (problems.Count > 0)
        {
            await output.WriteLineAsync("nothing was migrated:");
            foreach (var problem in problems)
            {
                await output.WriteLineAsync($"  {problem}");
            }

            return 2;
        }

        await using var redis = await ConnectionMultiplexer.ConnectAsync(settings.RedisConnectionString);
        using var http = new HttpClient { BaseAddress = new Uri(settings.Accounts.BaseUrl.TrimEnd('/') + "/") };
        if (asked.DryRun)
        {
            await output.WriteLineAsync("A dry run: this is what would be done, and nothing is written.");
        }

        Household household;
        var reports = new List<StepReport>();
        try
        {
            (household, var people) = await new AccountsImport(http, settings.Accounts.Credential)
                .ImportAsync(file, asked.Operator, asked.Owner, asked.Sugary, asked.DryRun, ct);
            reports.Add(people);
            await WriteAsync(people, output);
        }
        catch (MigrationRefusedException ex)
        {
            await output.WriteLineAsync($"nothing was migrated: {ex.Message}");
            return 1;
        }

        foreach (var step in Steps(settings, redis, stateKey))
        {
            var report = await step.RunAsync(household, asked.DryRun, ct);
            reports.Add(report);
            await WriteAsync(report, output);
        }

        var changes = reports.Sum(r => r.Changes);
        var left = reports.Sum(r => r.Problems.Count);
        await output.WriteLineAsync(
            changes == 0
                ? "Nothing changed: everything listed was already migrated."
                : $"{changes} change{(changes == 1 ? "" : "s")}{(asked.DryRun ? " would be made" : " made")}.");
        if (left > 0)
        {
            await output.WriteLineAsync($"{left} thing{(left == 1 ? " was" : "s were")} left where it was; see above.");
        }

        return left > 0 ? 1 : 0;
    }

    // In the order a person would want them done if the run stopped part way: what they told the agent,
    // then their logins, their phones, their schedules, their topics, their voice and their log.
    private static IReadOnlyList<IMigrationStep> Steps(MigrationSettings settings, IConnectionMultiplexer redis, byte[]? stateKey) =>
    [
        // The embedding service is never called; its options carry the index's width.
        new MemoryBuckets(new RedisStackMemoryStore(redis, new EmbeddingOptions
        {
            BaseAddress = "http://unused.invalid/", Model = "unused", Dimension = settings.MemoryDimension
        })),
        new RememberedLogins(redis, new RedisPersonStateStore(redis, stateKey)),
        new PushSubscriptions(redis)
    ];

    private static async Task WriteAsync(StepReport report, TextWriter output)
    {
        await output.WriteLineAsync(report.Title);
        foreach (var line in report.Lines.DefaultIfEmpty("nothing to move"))
        {
            await output.WriteLineAsync($"  {line}");
        }

        foreach (var problem in report.Problems)
        {
            await output.WriteLineAsync($"  left as it was: {problem}");
        }
    }

    private sealed record Arguments(string People, string Operator, string Owner, string Sugary, bool DryRun)
    {
        public static Arguments? Parse(IReadOnlyList<string> args)
        {
            var dryRun = args.Contains("--dry-run");
            var rest = args.Where(a => a != "--dry-run").ToList();
            if (rest.Count % 2 != 0)
            {
                return null;
            }

            var named = rest.Chunk(2).ToList();
            if (named.Any(pair => !pair[0].StartsWith("--", StringComparison.Ordinal))
                || named.GroupBy(pair => pair[0]).Any(g => g.Count() > 1))
            {
                return null;
            }

            var values = named.ToDictionary(pair => pair[0], pair => pair[1]);
            return values.Keys.Order().SequenceEqual(["--operator", "--owner", "--people", "--sugary"])
                ? new Arguments(values["--people"], values["--operator"], values["--owner"], values["--sugary"], dryRun)
                : null;
        }
    }
}