using Domain.Contracts;
using Domain.DTOs.FileSystem;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

// The bytes of an image the model read, resting where the agent can recycle without going blind
// mid-conversation. A hash rather than a serialized record, because the bytes go in as bytes: JSON
// would base64 them for no reason, and a string round trip would corrupt them outright.
public sealed class RedisReadImageStore(IConnectionMultiplexer redis) : IReadImageStore
{
    private const string KeyPrefix = "readimg:";
    private const string PathField = "path";
    private const string MediaTypeField = "mediaType";
    private const string BytesField = "bytes";

    // The backstop, not the bound. Hydration deletes an image on the send it drops out of view, so
    // this only catches a conversation that stopped before that send ever happened.
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(24);

    public static string KeyFor(string conversationId, string callId) =>
        $"{KeyPrefix}{conversationId}:{callId}";

    public async Task PutAsync(string conversationId, string callId, ReadImage image, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var key = KeyFor(conversationId, callId);

        await db.HashSetAsync(key,
        [
            new HashEntry(PathField, image.VirtualPath),
            new HashEntry(MediaTypeField, image.MediaType),
            new HashEntry(BytesField, image.Bytes)
        ]);
        await db.KeyExpireAsync(key, Horizon);
    }

    public async Task<ReadImage?> GetAsync(string conversationId, string callId, CancellationToken ct)
    {
        var entries = await redis.GetDatabase().HashGetAllAsync(KeyFor(conversationId, callId));
        if (entries.Length == 0)
        {
            return null;
        }

        var fields = entries.ToDictionary(e => e.Name.ToString(), e => e.Value);
        if (!fields.TryGetValue(BytesField, out var bytes) || (byte[]?)bytes is not { } data)
        {
            return null;
        }

        return new ReadImage
        {
            VirtualPath = fields.GetValueOrDefault(PathField).ToString(),
            MediaType = fields.GetValueOrDefault(MediaTypeField).ToString(),
            Bytes = data
        };
    }

    public Task DeleteAsync(string conversationId, string callId, CancellationToken ct) =>
        redis.GetDatabase().KeyDeleteAsync(KeyFor(conversationId, callId));
}