using StackExchange.Redis;

namespace HouseholdMigration;

// The two Redises the move reads and writes, by connection string: the deployment's, given for the
// run alone (HOMESTATE__FROM, never in the kit's files — its password is the deployment's), and the
// tenant kit's (HOMESTATE__TO, from the kit's compose service).
public record HomeStateSettings
{
    public string From { get; init; } = "";

    public string To { get; init; } = "";
}

// Carries the state a household's own servers kept in the deployment's Redis while the deployment ran
// them into the tenant kit's, once, when the home moves (TenantKit/README.md):
//
//   docker compose -f TenantKit/docker-compose.yml --profile move run --rm \
//     -e HOMESTATE__FROM="127.0.0.1:6379,password=<the deployment's>" state-move [--dry-run]
//
// Each key goes as it is, bytes and expiry — a remembered login is sealed and stays sealed under the
// same key, a one-time link keeps the minutes it had left. It copies and never overwrites: a key the
// kit already holds is the kit's, newer than the deployment's, so a second run copies nothing twice
// and a person who logged in again through the kit loses nothing. The download routing's index is
// the one key both sides add to, so it is merged. Nothing else of the deployment's moves.
public static class HomeStateMove
{
    public const string Verb = "move-home-state";

    private const string Usage = $"usage: {Verb} [--dry-run]  (HOMESTATE__FROM, HOMESTATE__TO: the two Redises)";

    // What the home's servers keep, by the stores that keep it: web search's remembered logins
    // (RedisPersonStateStore) and one-time links (OneTimeLinks), the library's download routing
    // (RedisDownloadRoutingStore) with the index it lists them by.
    private static readonly IReadOnlyList<Family> _families =
    [
        new("remembered logins", "websearch:person:*"),
        new("one-time links", "websearch:link:*"),
        new("download routing", "download-routing:*", Index: "download-routing")
    ];

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, HomeStateSettings settings, TextWriter output, CancellationToken ct)
    {
        if (args is not ([] or ["--dry-run"]))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        var missing = new[] { ("HOMESTATE__FROM", settings.From), ("HOMESTATE__TO", settings.To) }
            .Where(s => s.Item2.Length == 0)
            .Select(s => $"  {s.Item1} is not set")
            .ToList();
        if (missing.Count > 0)
        {
            await output.WriteLineAsync(string.Join(output.NewLine, ["nothing was copied:", .. missing]));
            return 2;
        }

        var dryRun = args is ["--dry-run"];
        await using var from = await ConnectionMultiplexer.ConnectAsync(settings.From);
        await using var to = await ConnectionMultiplexer.ConnectAsync(settings.To);
        await output.WriteLineAsync(dryRun
            ? "A dry run: this is what would be copied from the deployment's Redis into the kit's, and nothing is written."
            : "Copying the home's state from the deployment's Redis into the kit's:");
        foreach (var family in _families)
        {
            var (copied, already) = await CopyAsync(family, from, to, dryRun, ct);
            var merged = family.Index is not null ? await MergeIndexAsync(family.Index, from, to, dryRun) : 0;
            await output.WriteLineAsync(
                $"  {family.Name}: {copied} {(dryRun ? "to copy" : "copied")}"
                + (already > 0 ? $", {already} already in the kit" : "")
                + (merged > 0 ? $", {merged} {(dryRun ? "to add" : "added")} to its index" : ""));
        }

        return 0;
    }

    private static async Task<(int Copied, int Already)> CopyAsync(
        Family family, IConnectionMultiplexer from, IConnectionMultiplexer to, bool dryRun, CancellationToken ct)
    {
        var source = from.GetDatabase();
        var target = to.GetDatabase();
        var (copied, already) = (0, 0);
        await foreach (var key in from.GetServer(from.GetEndPoints()[0]).KeysAsync(source.Database, family.Pattern).WithCancellation(ct))
        {
            if (await target.KeyExistsAsync(key))
            {
                already++;
                continue;
            }

            // Gone between the scan and the dump: a link that ran out, which moves as nothing.
            if (await source.KeyDumpAsync(key) is not { } dump)
            {
                continue;
            }

            if (!dryRun)
            {
                try
                {
                    await target.KeyRestoreAsync(key, dump, await source.KeyTimeToLiveAsync(key));
                }
                catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYKEY", StringComparison.Ordinal))
                {
                    already++;
                    continue;
                }
            }

            copied++;
        }

        return (copied, already);
    }

    // The members the kit's index does not have yet; the kit's own stay.
    private static async Task<int> MergeIndexAsync(string index, IConnectionMultiplexer from, IConnectionMultiplexer to, bool dryRun)
    {
        var target = to.GetDatabase();
        var kit = (await target.SetMembersAsync(index)).ToHashSet();
        var missing = (await from.GetDatabase().SetMembersAsync(index)).Where(member => !kit.Contains(member)).ToArray();
        if (!dryRun && missing.Length > 0)
        {
            await target.SetAddAsync(index, missing);
        }

        return missing.Length;
    }

    private sealed record Family(string Name, string Pattern, string? Index = null);
}