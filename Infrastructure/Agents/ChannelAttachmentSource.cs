using System.Collections.Concurrent;
using Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// The agent's way to an attachment's bytes: ask the channel that took the file, the same way it
// asks that channel to send a reply. Nothing is mounted, so there is no other route (ADR 0021).
//
// Answers are cached because a reference is immutable — an upload store never rewrites a file, it
// only sweeps one, and a Telegram file handle names one file for as long as the bot exists. It is
// also the only cache there is on the Telegram side, where a fetch is an internet round trip
// rather than a disk read (ADR 0022). Without a cache the whole file crosses MCP again on every
// function-calling iteration of a turn, which for a 20 MB PDF and eight tool calls is eight base64
// round trips on the critical path before the model says anything.
public sealed class ChannelAttachmentSource(
    IEnumerable<IChannelConnection> connections,
    ILogger<ChannelAttachmentSource>? logger = null) : IAttachmentSource
{
    // Bounded by count rather than bytes, and small: a turn hydrates the attachments inside its
    // window, and holding much more than that is holding files nobody is about to ask for.
    private const int Capacity = 16;

    private readonly ConcurrentDictionary<string, Task<byte[]?>> _cache = new();
    private readonly ConcurrentQueue<string> _order = new();

    public Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct) =>
        FetchAsync(channelId, null, attachmentId, ct);

    // Asked of the connection that took the file: a household's own server's for its household, the
    // deployment's otherwise — never another household's server of the same name.
    public Task<byte[]?> FetchAsync(string channelId, string? householdId, string attachmentId, CancellationToken ct)
    {
        var connection = connections.Find(channelId, householdId);
        if (connection is null)
        {
            logger?.LogWarning(
                "Attachment {AttachmentId} names channel {ChannelId} of household {Household}, which this agent has no connection to",
                attachmentId, channelId, householdId ?? "none");
            return Task.FromResult<byte[]?>(null);
        }

        var key = $"{channelId}/{householdId}/{attachmentId}";
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