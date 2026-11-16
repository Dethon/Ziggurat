using Domain.DTOs.Metrics;
using JetBrains.Annotations;

namespace Domain.DTOs.Admin;

// A household's usage over one calendar month (yyyy-MM, in UTC, as the deployment's metrics are
// kept by day): tokens and cost per agent and per person, and its use of the shared Lemonade.
[PublicAPI]
public sealed record AdminUsage(string Month, IReadOnlyList<UsageLine> Agents, IReadOnlyList<UsageLine> People, LemonadeUsage Lemonade)
{
    public const string MonthFormat = "yyyy-MM";
}