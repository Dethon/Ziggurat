using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Settings;

namespace McpChannelSignalR.Attachments;

// Where an attachment's bytes rest between being sent and being read. A holding area and not a
// place anyone works: nothing is edited here, nothing is found by looking, and its contents are
// reached only by naming a reference.
//
// One directory per attachment, under one directory per conversation. The per-attachment
// directory is what lets the file keep the name the person gave it without two `scan.pdf`s in
// one conversation colliding, and the per-conversation directory is what deleting a topic
// removes.
public sealed class AttachmentStore(
    AttachmentSettings settings,
    RetentionSettings retention,
    TimeProvider timeProvider,
    ILogger<AttachmentStore> logger)
{
    private const string MetadataFileName = "meta.json";

    public sealed record Record(AttachmentReference Reference, string Path, string? SpaceSlug, DateTimeOffset StoredAt);

    private sealed record Metadata(
        string FileName,
        string MediaType,
        long SizeBytes,
        string ConversationId,
        string? SpaceSlug,
        DateTimeOffset StoredAt);

    public async Task<AttachmentReference> SaveAsync(
        string conversationId,
        string? spaceSlug,
        string fileName,
        string mediaType,
        Stream content,
        CancellationToken ct)
    {
        var safeName = SafeFileName(fileName);
        var attachmentId = Guid.NewGuid().ToString("N");
        var id = $"{ConversationKey(conversationId)}/{attachmentId}";
        var directory = Path.Combine(settings.StoragePath, ConversationKey(conversationId), attachmentId);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, safeName);
        long size;
        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, ct);
            size = file.Length;
        }

        var metadata = new Metadata(
            safeName, mediaType, size, conversationId, spaceSlug, timeProvider.GetUtcNow());
        await File.WriteAllTextAsync(
            Path.Combine(directory, MetadataFileName), JsonSerializer.Serialize(metadata), ct);

        return new AttachmentReference
        {
            Id = id,
            FileName = safeName,
            MediaType = mediaType,
            SizeBytes = size
        };
    }

    public Record? Find(string id)
    {
        var directory = ResolveDirectory(id);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        var metadataPath = Path.Combine(directory, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataPath));
        if (metadata is null)
        {
            return null;
        }

        var path = Path.Combine(directory, metadata.FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var reference = new AttachmentReference
        {
            Id = id,
            FileName = metadata.FileName,
            MediaType = metadata.MediaType,
            SizeBytes = metadata.SizeBytes
        };
        return new Record(reference, path, metadata.SpaceSlug, metadata.StoredAt);
    }

    public async Task<byte[]?> ReadBytesAsync(string id, CancellationToken ct)
    {
        var record = Find(id);
        return record is null ? null : await File.ReadAllBytesAsync(record.Path, ct);
    }

    public void DeleteConversation(string conversationId)
    {
        var directory = Path.Combine(settings.StoragePath, ConversationKey(conversationId));
        TryDeleteDirectory(directory);
    }

    // Everything topic deletion never reaches: conversations nobody deletes, and files uploaded
    // for a message that was abandoned before it was sent. Both look the same from here — an
    // attachment directory older than the window — so both go the same way.
    public int Sweep()
    {
        if (!Directory.Exists(settings.StoragePath))
        {
            return 0;
        }

        // The conversation's own horizon, not a clock of its own: a topic and the files sent
        // to it are removed together rather than the files going eleven months early.
        var cutoff = timeProvider.GetUtcNow() - retention.AttachmentRetention;
        var swept = Directory.EnumerateDirectories(settings.StoragePath)
            .SelectMany(Directory.EnumerateDirectories)
            .Where(directory => StoredAt(directory) is { } storedAt && storedAt <= cutoff)
            .Count(TryDeleteDirectory);

        Directory.EnumerateDirectories(settings.StoragePath)
            .Where(conversation => !Directory.EnumerateFileSystemEntries(conversation).Any())
            .ToList()
            .ForEach(conversation => TryDeleteDirectory(conversation));

        return swept;
    }

    private DateTimeOffset? StoredAt(string directory)
    {
        var metadataPath = Path.Combine(directory, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            // An attachment directory with no metadata was never completed. Treat it as old
            // enough to sweep rather than leaving it forever.
            return DateTimeOffset.MinValue;
        }

        try
        {
            return JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataPath))?.StoredAt;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unreadable attachment metadata at {Path}; sweeping it", metadataPath);
            return DateTimeOffset.MinValue;
        }
    }

    private bool TryDeleteDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deleting {Directory} from the upload store failed", directory);
            return false;
        }
    }

    private string? ResolveDirectory(string id)
    {
        var segments = id.Split('/');
        if (segments.Length != 2 || segments.Any(s => !IsSafeSegment(s)))
        {
            return null;
        }

        return Path.Combine(settings.StoragePath, segments[0], segments[1]);
    }

    public static string ConversationKey(string conversationId) =>
        AttachmentEndpointPaths.ConversationDirectory(conversationId);

    private static bool IsSafeSegment(string segment) =>
        segment.Length > 0
        && segment != "."
        && segment != ".."
        && !segment.Contains('/')
        && !segment.Contains('\\')
        && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var cleaned = new string(name
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c))
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "attachment" : cleaned;
    }
}