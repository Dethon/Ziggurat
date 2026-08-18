using Domain.Contracts;
using Domain.DTOs.FileSystem;

namespace Domain.Agents;

// Where a read image's bytes come from on the way to the model, which conversation they were keyed
// under, and whether the model this send resolved to can take an image at all. One type because the
// three always travel together and are always answered per send.
//
// Either half of the lookup may be missing — a host with no store, a turn carrying no conversation —
// and neither is a mode: the answer is the same as a store that lost the bytes, so the model is told
// which image it cannot see rather than being left waiting for one that never arrives.
public sealed record ReadImageContext(
    IReadImageStore? Store,
    string? ConversationId,
    bool ModelAcceptsImages = true)
{
    public static ReadImageContext None { get; } = new(null, null);

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