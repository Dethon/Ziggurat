using System.Collections.Concurrent;
using Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// The agent's way to an attachment's bytes: ask the channel that took the file, the same way it
// asks that channel to send a reply. Nothing is mounted, so there is no other route (ADR 0021).
//
// Answers are cached because a reference is immutable — the upload store never rewrites a file,
// it only sweeps one. Without a cache the whole file crosses MCP again on every function-calling
// iteration of a turn, which for a 20 MB PDF and eight tool calls is eight base64 round trips on
// the critical path before the model says anything.
public sealed class ChannelAttachmentSource(
    IReadOnlyList<IChannelConnection> connections,
    ILogger<ChannelAttachmentSource>? logger = null) : IAttachmentSource
{
    // Bounded by count rather than bytes, and small: a turn hydrates the attachments inside its
    // window, and holding much more than that is holding files nobody is about to ask for.
    private const int Capacity = 16;

    private readonly ConcurrentDictionary<string, Task<byte[]?>> _cache = new();
    private readonly ConcurrentQueue<string> _order = new();

    public Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct)
    {
        var connection = connections.FirstOrDefault(c => c.ChannelId == channelId);
        if (connection is null)
        {
            logger?.LogWarning(
                "Attachment {AttachmentId} names channel {ChannelId}, which this agent has no connection to",
                attachmentId, channelId);
            return Task.FromResult<byte[]?>(null);
        }

        var key = $"{channelId}/{attachmentId}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // The fetch itself is cached, not its result, so concurrent hydrations of one attachment
        // share a single round trip rather than racing two. It goes in before it is awaited,
        // because the forget below has to find it there.
        var fetch = connection.FetchAttachmentAsync(attachmentId, ct);
        if (!_cache.TryAdd(key, fetch))
        {
            return _cache[key];
        }

        _order.Enqueue(key);
        Evict();
        return ForgetIfEmptyAsync(key, fetch);
    }

    // A file that could not be had is not cached: the next turn may find the channel back up, and
    // a cached nothing would keep answering with a placeholder for a file that is still there.
    private async Task<byte[]?> ForgetIfEmptyAsync(string key, Task<byte[]?> fetch)
    {
        try
        {
            var bytes = await fetch;
            if (bytes is null)
            {
                _cache.TryRemove(key, out _);
            }

            return bytes;
        }
        catch
        {
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    private void Evict()
    {
        while (_cache.Count > Capacity && _order.TryDequeue(out var oldest))
        {
            _cache.TryRemove(oldest, out _);
        }
    }
}