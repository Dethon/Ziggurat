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
    private readonly ConcurrentDictionary<string, DictationScope> _dictations = new();

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

    // No slot counter and no conversation: a dictation produces composer text, so it must not
    // spend one of a message's attachment slots or force a conversation into existence the way
    // picking a file does.
    private sealed record DictationScope(string SpaceSlug, DateTimeOffset ExpiresAt);

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

    public DictationTicket MintDictation(string spaceSlug)
    {
        Prune();
        var token = NewToken();
        var expiresAt = timeProvider.GetUtcNow().AddSeconds(settings.TicketTtlSeconds);
        _dictations[token] = new DictationScope(spaceSlug, expiresAt);
        return new DictationTicket(token, expiresAt);
    }

    public bool ResolvesDictation(string? token, string? spaceSlug)
    {
        if (string.IsNullOrWhiteSpace(token) || !_dictations.TryGetValue(token, out var scope))
        {
            return false;
        }

        return scope.ExpiresAt > timeProvider.GetUtcNow() && scope.SpaceSlug == spaceSlug;
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
        Prune(_uploads, scope => scope.ExpiresAt, now);
        Prune(_downloads, scope => scope.ExpiresAt, now);
        Prune(_dictations, scope => scope.ExpiresAt, now);
    }

    private static void Prune<TScope>(
        ConcurrentDictionary<string, TScope> scopes, Func<TScope, DateTimeOffset> expiry, DateTimeOffset now)
    {
        var stale = scopes.Where(x => expiry(x.Value) <= now).Select(x => x.Key).ToList();
        foreach (var token in stale)
        {
            scopes.TryRemove(token, out _);
        }
    }

    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}