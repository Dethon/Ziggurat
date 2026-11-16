using System.Globalization;
using System.Text.RegularExpressions;

namespace Domain.Metrics;

// Every Redis key the dashboard's roll-ups live under, in one place: the collector writes them, the
// query service reads them, and the household migration finds the ones from before households.
//
// A roll-up is kept per scope: a household's id, or Deployment for the events no household's turn
// made. The household ids the accounts service mints are 32 hex characters, so the literal can be
// no household. A daily key has four segments (metrics:{family}:{scope}:{date}) and one from before
// households had three, which is all the migration tells them apart by — no family may hold ':'.
public static partial class MetricKeys
{
    public const string Deployment = "deployment";

    // Every scope an event has been rolled up under, so a read over all households knows where to look.
    public const string Scopes = "metrics:scopes";

    public const string Totals = "totals";
    public const string Tokens = "tokens";
    public const string Tools = "tools";
    public const string Errors = "errors";
    public const string Schedules = "schedules";
    public const string Outposts = "outposts";
    public const string Latency = "latency";
    public const string MemoryRecall = "memory-recall";
    public const string MemoryExtraction = "memory-extraction";
    public const string MemoryJudgment = "memory-judgment";
    public const string MemoryDreaming = "memory-dreaming";
    public const string Truncations = "truncations";
    public const string Modals = "modals";
    public const string Skills = "skills";
    public const string ExecScreen = "execscreen";
    public const string Voice = "voice";
    public const string Lemonade = "lemonade";

    // The recent-errors list from before households, which held everyone's.
    public const string LegacyRecentErrors = "metrics:errors:recent";

    public static string ScopeOf(string? tenantId) => string.IsNullOrEmpty(tenantId) ? Deployment : tenantId;

    public static string? TenantOf(string scope) => scope == Deployment ? null : scope;

    public static string Daily(string family, string scope, DateOnly date) =>
        $"metrics:{family}:{scope}:{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    public static string Daily(string family, string scope, DateTimeOffset at) =>
        Daily(family, scope, DateOnly.FromDateTime(at.UtcDateTime));

    public static string RecentErrors(string scope) => $"{LegacyRecentErrors}:{scope}";

    // A daily key from before households: metrics:{family}:{yyyy-MM-dd}, everyone's in one.
    public static (string Family, DateOnly Date)? Legacy(string key) =>
        LegacyDaily().Match(key) is { Success: true } match
        && DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? (match.Groups["family"].Value, date)
            : null;

    [GeneratedRegex(@"^metrics:(?<family>[a-z-]+):(?<date>\d{4}-\d{2}-\d{2})$")]
    private static partial Regex LegacyDaily();
}