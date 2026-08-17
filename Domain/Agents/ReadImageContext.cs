using Domain.Contracts;
using Domain.DTOs.FileSystem;

namespace Domain.Agents;

// Where a read image's bytes come from on the way to the model, which conversation they were keyed
// under, and the zone the injected turn is decorated in. One type because the three always travel
// together and are always answered per send.
//
// Either half of the lookup may be missing — a host with no store, a turn carrying no conversation —
// and neither is a mode: the answer is the same as a store that lost the bytes, so the model is told
// which image it cannot see rather than being left waiting for one that never arrives.
public sealed record ReadImageContext(
    IReadImageStore? Store,
    string? ConversationId,
    TimeZoneInfo LocalTimeZone)
{
    public static ReadImageContext None { get; } = new(null, null, TimeZoneInfo.Utc);

    private bool CanReach => Store is not null && !string.IsNullOrWhiteSpace(ConversationId);

    public async Task<ReadImage?> FetchAsync(string callId, CancellationToken ct) =>
        CanReach ? await Store!.GetAsync(ConversationId!, callId, ct) : null;

    public async Task ForgetAsync(string callId, CancellationToken ct)
    {
        if (CanReach)
        {
            await Store!.DeleteAsync(ConversationId!, callId, ct);
        }
    }
}