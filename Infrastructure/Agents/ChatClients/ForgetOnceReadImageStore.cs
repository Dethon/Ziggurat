using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs.FileSystem;

namespace Infrastructure.Agents.ChatClients;

// The envelope of a dropped image stays in the history for the rest of the conversation, so the
// send that deletes its bytes would otherwise be every send from then on — one delete per aged
// image per send, forever. Hydration keeps its rule simple (out of view means forget) and this
// wrapper makes the delete reach the store once.
//
// Safe to memoise because a key is never reused: a re-read of the same file happens under a new
// tool call id, so a put after a delete of the same key cannot occur.
internal sealed class ForgetOnceReadImageStore(IReadImageStore inner) : IReadImageStore
{
    private readonly ConcurrentDictionary<string, byte> _forgotten = new();

    public Task PutAsync(string conversationId, string callId, ReadImage image, CancellationToken ct) =>
        inner.PutAsync(conversationId, callId, image, ct);

    public Task<ReadImage?> GetAsync(string conversationId, string callId, CancellationToken ct) =>
        inner.GetAsync(conversationId, callId, ct);

    public Task DeleteAsync(string conversationId, string callId, CancellationToken ct) =>
        _forgotten.TryAdd($"{conversationId}:{callId}", 0)
            ? inner.DeleteAsync(conversationId, callId, ct)
            : Task.CompletedTask;
}