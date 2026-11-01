using Domain.Metrics;

namespace Dashboard.Client.Metrics;

// Which household the dashboard's metric pages show: a household's id, MetricKeys.Deployment for the
// deployment's own events, or null for everybody's. Every read asks the API for it (?household=), and
// a live push for any other household is dropped (MetricsHubBinder), since the hub sends everybody's.
// Kept for the tab's life, not saved: a reload shows everybody's again.
public sealed class HouseholdFilter
{
    public string? Selected { get; private set; }

    public void Select(string? household) => Selected = string.IsNullOrEmpty(household) ? null : household;

    // Whether an event of this household (null: the deployment's own) is one the pages show now.
    public bool Admits(string? tenantId) => Selected is null || MetricKeys.ScopeOf(tenantId) == Selected;

    // The read's query, joined onto a path that may already carry one.
    public string On(string path) =>
        Selected is null ? path : $"{path}{(path.Contains('?') ? '&' : '?')}household={Uri.EscapeDataString(Selected)}";
}