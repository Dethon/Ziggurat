using Domain.Contracts;
using Domain.DTOs.Metrics;

namespace Infrastructure.Metrics;

// An agent is built for one household (AgentSpec.TenantId), so everything it publishes is that
// household's; a worker is built for its parent's.
public sealed class AgentMetricsPublisher(IMetricsPublisher inner, string agentId, string? tenantId = null) : IMetricsPublisher
{
    public void Publish(MetricEvent metricEvent) =>
        inner.Publish(metricEvent with { AgentId = agentId, TenantId = tenantId ?? metricEvent.TenantId });
}