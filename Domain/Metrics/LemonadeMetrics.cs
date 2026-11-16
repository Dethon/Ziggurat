using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Domain.Metrics;

// Counting a use of the shared Lemonade, in the one unit each use is counted in. The household is the
// publisher's to say (HouseholdMetricsPublisher) or the event's own; a kit server's is the relay's.
public static class LemonadeMetrics
{
    // The channel an embedding is counted under: recall, extraction and dreaming all embed for memory.
    public const string Memory = "memory";

    public static void RecordEmbedded(
        this IMetricsPublisher metrics, int texts, string? conversationId = null, string? agentId = null) =>
        metrics.Publish(new LemonadeUsageEvent
        {
            Use = LemonadeUse.Embedding,
            Amount = texts,
            Channel = Memory,
            ConversationId = conversationId,
            AgentId = agentId
        });
}