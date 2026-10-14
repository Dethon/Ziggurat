using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpServerSugary.Settings;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Channel;

// Where the photos of a meal rest between the app sending them and the agent asking for them, for
// as long as hydration might ask again (ADR-0020). A holding area, never a place anyone works: it is
// not a mount (ADR-0021), nothing lists it, and its contents are reached only by naming a reference.
//
// Files on disk in the server's own volume, the shape the WebChat upload store has: one directory
// per attachment under one directory per conversation, the bytes beside a meta.json. Not her log's
// database, which holds what she saved and is what she backs up; a photo nobody saved does not
// belong in it, and nothing here is ever joined or rendered.
public sealed class UploadStore(
    UploadSettings settings, RetentionSettings retention, TimeProvider clock, ILogger<UploadStore> logger)
{
    private const string MetadataFileName = "meta.json";

    private sealed record Metadata(string FileName, string MediaType, long SizeBytes, DateTimeOffset StoredAt);

    public async Task<AttachmentReference> SaveAsync(
        string conversationId, string fileName, string mediaType, byte[] bytes, CancellationToken ct)
    {
        // Laid out as WebChat lays the same conversation out, so the copy WebChat keeps for a review
        // sits where deleting that topic removes its files.
        var conversation = SafeSegment(AttachmentEndpointPaths.ConversationDirectory(conversationId))
            ?? throw new ArgumentException($"'{conversationId}' cannot name a directory.", nameof(conversationId));
        var attachmentId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(settings.StoragePath, conversation, attachmentId);
        Directory.CreateDirectory(directory);

        var name = SafeFileName(fileName);
        await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, ct);
        var metadata = new Metadata(name, mediaType, bytes.LongLength, clock.GetUtcNow());
        await File.WriteAllTextAsync(
            Path.Combine(directory, MetadataFileName), JsonSerializer.Serialize(metadata), ct);

        return new AttachmentReference
        {
            Id = $"{conversation}/{attachmentId}",
            FileName = name,
            MediaType = mediaType,
            SizeBytes = bytes.LongLength
        };
    }

    // Null for anything this store does not hold: swept, never stored, or an id shaped to reach
    // outside it. The caller cannot do anything different about the three.
    public async Task<byte[]?> ReadBytesAsync(string id, CancellationToken ct)
    {
        var directory = Resolve(id);
        if (directory is null || ReadMetadata(directory) is not { } metadata)
        {
            return null;
        }

        var path = Path.Combine(directory, metadata.FileName);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    // Age alone decides, unlike WebChat's store, which also waits for the topic to be gone: a meal's
    // conversation is a handful of turns over a few minutes, so the window, the deployment's
    // attachment retention, outlasts its hydration distance by months rather than by luck.
    public int Sweep()
    {
        if (!Directory.Exists(settings.StoragePath))
        {
            return 0;
        }

        var cutoff = clock.GetUtcNow() - retention.AttachmentRetention;
        var conversations = Directory.EnumerateDirectories(settings.StoragePath).ToList();
        var swept = conversations
            .SelectMany(Directory.EnumerateDirectories)
            .Where(directory => (ReadMetadata(directory)?.StoredAt ?? DateTimeOffset.MinValue) <= cutoff)
            .ToList()
            .Count(TryDelete);

        conversations
            .Where(conversation => !Directory.EnumerateFileSystemEntries(conversation).Any())
            .ToList()
            .ForEach(conversation => TryDelete(conversation));
        return swept;
    }

    private string? Resolve(string id)
    {
        var segments = id.Split('/');
        return segments.Length == 2 && segments.All(s => SafeSegment(s) is not null)
            ? Path.Combine(settings.StoragePath, segments[0], segments[1])
            : null;
    }

    // Null is an attachment that was never completed, which the sweep treats as old enough to go.
    private Metadata? ReadMetadata(string directory)
    {
        var path = Path.Combine(directory, MetadataFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Metadata>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unreadable upload metadata at {Path}", path);
            return null;
        }
    }

    private bool TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deleting {Directory} from the upload store failed", directory);
            return false;
        }
    }

    private static string? SafeSegment(string segment) =>
        segment.Length > 0
        && segment != "."
        && segment != ".."
        && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !segment.Contains('\\')
            ? segment
            : null;

    private static string SafeFileName(string fileName)
    {
        var name = new string(Path.GetFileName(fileName)
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c))
            .ToArray());
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." || name == MetadataFileName
            ? "photo"
            : name;
    }
}