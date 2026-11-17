using System.Text.Json.Serialization;
using Domain.Channels;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public sealed record ChannelReceiveResult
{
    public IReadOnlyList<ChannelInboxItem> Items { get; init; } = [];

    // The number of this batch, for a poller that named itself (ChannelReceiveTool's poller): it names it as
    // received on its next poll, and a poll naming an earlier one is answered with it again. Absent for a
    // poller that named nothing and for an empty batch, so a poller from before reads what it always read.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Batch { get; init; }
}