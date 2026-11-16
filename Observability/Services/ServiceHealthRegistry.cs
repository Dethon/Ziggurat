using StackExchange.Redis;

namespace Observability.Services;

// The roster of services the dashboard draws a health tile for. Scores are the last time we had
// reason to believe a service still exists, NOT the last time it was healthy — reachability lives
// in the TTL'd metrics:health:<member> key. Scoring registration rather than health is what lets a
// configured probe target sit on the dashboard as a red tile for as long as it is down, while a
// service that was retired outright (the Wyoming STT/TTS pair) falls off on its own. The predecessor
// was a plain set that nothing ever removed from, so retired services stayed red forever.
//
// A member is the service's name for the deployment's own, and {household}:{service} for a
// household's server, so two households' voice servers are two tiles. The deployment's keep the
// shape they had: both keys expire on their own, so nothing needs moving.
public static class ServiceHealthRegistry
{
    public const string SeenKey = "metrics:health:seen";

    // Legacy never-pruned roster, dropped once on collector startup.
    public const string LegacyKey = "metrics:health:known";

    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static string Member(string service, string? tenantId) =>
        string.IsNullOrEmpty(tenantId) ? service : $"{tenantId}:{service}";

    public static (string Service, string? TenantId) Parse(string member) =>
        member.Split(':', 2) is [var tenantId, var service] ? (service, tenantId) : (member, null);

    public static string HealthKey(string member) => $"metrics:health:{member}";

    public static Task MarkSeenAsync(IDatabase db, string member, DateTimeOffset now) =>
        db.SortedSetAddAsync(SeenKey, member, now.ToUnixTimeSeconds());

    public static async Task<IReadOnlyList<string>> ListAsync(IDatabase db, DateTimeOffset now)
    {
        await db.SortedSetRemoveRangeByScoreAsync(
            SeenKey,
            double.NegativeInfinity,
            now.Subtract(Retention).ToUnixTimeSeconds(),
            Exclude.Stop);

        var members = await db.SortedSetRangeByRankAsync(SeenKey);
        return members.Select(m => m.ToString()).ToArray();
    }
}