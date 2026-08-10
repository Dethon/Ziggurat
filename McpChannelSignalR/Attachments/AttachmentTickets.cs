using System.Collections.Concurrent;
using System.Security.Cryptography;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Settings;

namespace McpChannelSignalR.Attachments;

// Who is allowed to put bytes on the upload store, and for how long. A ticket names one topic and
// expires; nothing else gets in. The per-message file count is counted here too, because the
// ticket is the only thing that spans the files of one message.
public sealed class AttachmentTickets(AttachmentSettings settings, TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, UploadScope> _uploads = new();
    private readonly ConcurrentDictionary<string, DownloadScope> _downloads = new();

    // A class, not a record: the slot counter mutates, and a scope's identity is the ticket that
    // resolved it rather than its values.
    public sealed class UploadScope(
        string topicId,
        string conversationId,
        string? spaceSlug,
        DateTimeOffset expiresAt)
    {
        private int _used;

        public string TopicId { get; } = topicId;
        public string ConversationId { get; } = conversationId;
        public string? SpaceSlug { get; } = spaceSlug;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public bool TryTakeSlot(int maximum) => Interlocked.Increment(ref _used) <= maximum;
    }

    private sealed record DownloadScope(string AttachmentId, DateTimeOffset ExpiresAt);

    public UploadTicket MintUpload(string topicId, string conversationId, string? spaceSlug)
    {
        Prune();
        var token = NewToken();
        var expiresAt = timeProvider.GetUtcNow().AddSeconds(settings.TicketTtlSeconds);
        _uploads[token] = new UploadScope(topicId, conversationId, spaceSlug, expiresAt);
        return new UploadTicket(token, expiresAt);
    }

    public DownloadTicket MintDownload(string attachmentId)
    {
        Prune();
        var token = NewToken();
        var expiresAt = timeProvider.GetUtcNow().AddSeconds(settings.TicketTtlSeconds);
        _downloads[token] = new DownloadScope(attachmentId, expiresAt);
        return new DownloadTicket(token, expiresAt);
    }

    // Null covers all three refusals the endpoint answers the same way: no such ticket, one that
    // has expired, and one minted for a different topic.
    public UploadScope? ResolveUpload(string? token, string? topicId)
    {
        if (string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(topicId)
            || !_uploads.TryGetValue(token, out var scope))
        {
            return null;
        }

        return scope.ExpiresAt <= timeProvider.GetUtcNow() || scope.TopicId != topicId ? null : scope;
    }

    public bool ResolvesDownload(string? token, string attachmentId)
    {
        if (string.IsNullOrWhiteSpace(token) || !_downloads.TryGetValue(token, out var scope))
        {
            return false;
        }

        return scope.ExpiresAt > timeProvider.GetUtcNow() && scope.AttachmentId == attachmentId;
    }

    private void Prune()
    {
        var now = timeProvider.GetUtcNow();
        var staleUploads = _uploads.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key);
        var staleDownloads = _downloads.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key);
        foreach (var token in staleUploads.ToList())
        {
            _uploads.TryRemove(token, out _);
        }

        foreach (var token in staleDownloads.ToList())
        {
            _downloads.TryRemove(token, out _);
        }
    }

    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}