using Domain.Contracts;
using Domain.DTOs.Metrics;

namespace Domain.Metrics;

// A publisher for the work of one household: every event it hands on names that household, unless
// the event already names one. Held by what acts for a household — a conversation group, a turn —
// so the household travels with the publisher rather than with every call site.
public sealed class HouseholdMetricsPublisher(IMetricsPublisher inner, string? tenantId) : IMetricsPublisher
{
    public void Publish(MetricEvent metricEvent) =>
        inner.Publish(metricEvent.TenantId is null && tenantId is not null
            ? metricEvent with { TenantId = tenantId }
            : metricEvent);
}

public static class HouseholdMetrics
{
    public static IMetricsPublisher ForHousehold(this IMetricsPublisher publisher, string? tenantId) =>
        string.IsNullOrEmpty(tenantId) ? publisher : new HouseholdMetricsPublisher(publisher, tenantId);
}