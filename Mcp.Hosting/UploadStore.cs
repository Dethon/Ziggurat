using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging;

namespace Mcp.Hosting;

// Where a channel server keeps it: inside the server's own volume, beside its data but never in it.
public interface IUploadStoreSettings
{
    string StoragePath { get; }
}

// Where the files a channel server sends the agent rest between the request that carried them and
// the agent asking for them, for as long as hydration might ask again (ADR-0020). A holding area,
// never a place anyone works: it is not a mount (ADR-0021), nothing lists it, and its contents are
// reached only by naming a reference.
//
// Files on disk in the server's own volume, the shape the WebChat upload store has: one directory
// per attachment under one directory per conversation, the bytes beside a meta.json. Not the server's
// database: a file nobody saved does not belong in what is backed up, and nothing here is ever joined
// or rendered.
//
// One store holds every household's files, so a file stored for a household is handed only to a read
// naming it and the conversation it was stored under: the agent signs each fetch for the turn it
// hydrates, so that is the household asking now, and the turn's conversation is the one whose history
// names the file — another app's or member's conversation of the same household is not. A file stored
// for none is handed to any read, as every file was before households were kept: Sugary's, whose
// histories already name files stored that way.
public sealed class UploadStore(
    IUploadStoreSettings settings, RetentionSettings retention, TimeProvider clock, ILogger<UploadStore> logger)
{
    private const string MetadataFileName = "meta.json";

    // An attachment's metadata is written after its bytes, so one without it may be a write still
    // under way (a file of a couple of hundred megabytes takes a moment). Past this it is one the
    // server died in the middle of, and goes without waiting out the window.
    private static readonly TimeSpan _unfinishedGrace = TimeSpan.FromHours(1);

    // The household is left out when there is none, so a file stored for none is described byte for
    // byte as the ones a running deployment's histories already name.
    private sealed record Metadata(
        string FileName, string MediaType, long SizeBytes, DateTimeOffset StoredAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Household = null);

    public Task<AttachmentReference> SaveAsync(
        string conversationId, string fileName, string mediaType, byte[] bytes, CancellationToken ct) =>
        StoreAsync(null, conversationId, fileName, mediaType, bytes, ct);

    public Task<AttachmentReference> SaveForHouseholdAsync(
        string household, string conversationId, string fileName, string mediaType, byte[] bytes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(household);
        return StoreAsync(household, conversationId, fileName, mediaType, bytes, ct);
    }

    private async Task<AttachmentReference> StoreAsync(
        string? household, string conversationId, string fileName, string mediaType, byte[] bytes, CancellationToken ct)
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
        var metadata = new Metadata(name, mediaType, bytes.LongLength, clock.GetUtcNow(), household);
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

    // Null for anything this store does not hand to this household and conversation: swept, never stored,
    // another household's or conversation's, or an id shaped to reach outside it. The caller cannot do
    // anything different about any of them.
    public async Task<byte[]?> ReadBytesAsync(string id, string? household, string? conversationId, CancellationToken ct)
    {
        var directory = Resolve(id);
        if (directory is null
            || ReadMetadata(directory) is not { } metadata
            || (metadata.Household is { } owner
                && (owner != household || !IsStoredUnder(id, conversationId))))
        {
            return null;
        }

        var path = Path.Combine(directory, metadata.FileName);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    // Files whose message never reached the agent (nobody was listening), taken back at once: nothing will
    // hydrate them, and a page retrying through an outage would otherwise fill the volume for a retention.
    public void Remove(IEnumerable<AttachmentReference> attachments) =>
        attachments
            .Select(attachment => Resolve(attachment.Id))
            .OfType<string>()
            .Where(Directory.Exists)
            .ToList()
            .ForEach(directory =>
            {
                TryDelete(directory);
                var conversation = Path.GetDirectoryName(directory)!;
                if (Directory.Exists(conversation) && !Directory.EnumerateFileSystemEntries(conversation).Any())
                {
                    TryDelete(conversation);
                }
            });

    // Age alone decides, unlike WebChat's store, which also waits for the topic to be gone: a channel
    // server's conversation is a handful of turns over minutes or hours, so the window, the
    // deployment's attachment retention, outlasts its hydration distance by months rather than by luck.
    public int Sweep()
    {
        if (!Directory.Exists(settings.StoragePath))
        {
            return 0;
        }

        var now = clock.GetUtcNow();
        var cutoff = now - retention.AttachmentRetention;
        var conversations = Directory.EnumerateDirectories(settings.StoragePath).ToList();
        var swept = conversations
            .SelectMany(Directory.EnumerateDirectories)
            .Where(directory => ReadMetadata(directory) is { } metadata
                ? metadata.StoredAt <= cutoff
                : Directory.GetLastWriteTimeUtc(directory) <= (now - _unfinishedGrace).UtcDateTime)
            .ToList()
            .Count(TryDelete);

        conversations
            .Where(conversation => !Directory.EnumerateFileSystemEntries(conversation).Any())
            .ToList()
            .ForEach(conversation => TryDelete(conversation));
        return swept;
    }

    // The id's first segment is the conversation's directory, spelled as StoreAsync spelled it.
    private static bool IsStoredUnder(string id, string? conversationId) =>
        conversationId is not null
        && SafeSegment(AttachmentEndpointPaths.ConversationDirectory(conversationId)) is { } conversation
        && id.StartsWith($"{conversation}/", StringComparison.Ordinal);

    private string? Resolve(string id)
    {
        var segments = id.Split('/');
        return segments.Length == 2 && segments.All(s => SafeSegment(s) is not null)
            ? Path.Combine(settings.StoragePath, segments[0], segments[1])
            : null;
    }

    // Null is an attachment that was never completed (or is still being written), which the sweep
    // judges by its directory's own age.
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