using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Exceptions;
using Domain.Tools.MusicAssistant;

namespace Infrastructure.Clients.MusicAssistant;

// Music Assistant's native websocket API. MA has no HTTP command endpoint — `/ws` is the only way
// in — and the calls we need (a podcast's episode list) exist nowhere in Home Assistant.
//
// A connection is opened per call and closed after it. Episode lookups happen once per user request
// at most, so a pooled connection would only add reconnect and re-auth state to maintain.
public sealed class MusicAssistantClient(string baseUrl, string token, TimeSpan? timeout = null)
    : IMusicAssistantClient
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<MaMediaItem>> SearchAsync(
        string query, string mediaType, int limit, CancellationToken ct = default)
    {
        var result = await SendAsync("music/search", new JsonObject
        {
            ["search_query"] = query,
            ["media_types"] = new JsonArray(mediaType),
            ["limit"] = limit
        }, ct);

        // Search returns one array per media type; we asked for exactly one, so take that bucket.
        var bucket = (result as JsonObject)?[Plural(mediaType)] as JsonArray;
        return Items(bucket);
    }

    public async Task<IReadOnlyList<MaMediaItem>> GetPodcastEpisodesAsync(
        MaUri podcast, CancellationToken ct = default)
    {
        var result = await SendAsync("music/podcasts/podcast_episodes", new JsonObject
        {
            ["item_id"] = podcast.ItemId,
            ["provider_instance_id_or_domain"] = podcast.Provider
        }, ct);

        return Items(result as JsonArray);
    }

    public async Task<MaQueuePosition?> GetQueuePositionAsync(string queueId, CancellationToken ct = default)
    {
        // `player_queues/get` is asked for one queue by id rather than filtering `player_queues/all`
        // client-side: a home can have a queue per speaker and only this one is being read.
        var result = await SendAsync("player_queues/get", new JsonObject
        {
            ["queue_id"] = queueId
        }, ct);

        if (result is not JsonObject queue
            || queue["elapsed_time"]?.GetValueKind() is not JsonValueKind.Number)
        {
            return null;
        }

        return new MaQueuePosition
        {
            ElapsedTime = queue["elapsed_time"]!.GetValue<double>(),
            // MA stamps this as unix seconds with a fractional part, not as a formatted date.
            LastUpdated = queue["elapsed_time_last_updated"]?.GetValueKind() is JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(
                    (long)(queue["elapsed_time_last_updated"]!.GetValue<double>() * 1000))
                : DateTimeOffset.MinValue,
            State = queue["state"]?.GetValueKind() is JsonValueKind.String
                ? queue["state"]!.GetValue<string>()
                : null
        };
    }

    private static string Plural(string mediaType) => mediaType switch
    {
        "podcast" => "podcasts",
        "track" => "tracks",
        "album" => "albums",
        "artist" => "artists",
        "playlist" => "playlists",
        "audiobook" => "audiobooks",
        "radio" => "radio",
        _ => mediaType + "s"
    };

    private static IReadOnlyList<MaMediaItem> Items(JsonArray? array) =>
        array is null
            ? []
            : array
                .OfType<JsonObject>()
                .Where(o => o["name"] is not null && o["uri"] is not null)
                .Select(o => new MaMediaItem
                {
                    Name = o["name"]!.GetValue<string>(),
                    Uri = o["uri"]!.GetValue<string>(),
                    DurationSeconds = o["duration"]?.GetValue<double>()
                })
                .ToList();

    private async Task<JsonNode?> SendAsync(string command, JsonObject args, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        var callCt = timeoutCts.Token;

        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(WebSocketUri(baseUrl), callCt);

            // MA greets every connection with an unsolicited server-info frame that carries no
            // message_id; read it off before correlating anything.
            await ReceiveAsync(socket, callCt);

            await RequestAsync(socket, "auth", "1", new JsonObject { ["token"] = token }, callCt);
            await RequestAsync(socket, command, "2", args, callCt);

            return await ReadResultAsync(socket, "2", callCt);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MusicAssistantException($"Music Assistant did not respond within {_timeout.TotalSeconds:0}s.");
        }
        catch (WebSocketException ex)
        {
            throw new MusicAssistantException($"Could not reach Music Assistant at {baseUrl}: {ex.Message}", ex);
        }
        finally
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // The call already produced its result; a failed goodbye is not the caller's problem.
                }
            }
        }
    }

    private static async Task RequestAsync(
        ClientWebSocket socket, string command, string messageId, JsonObject args, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["command"] = command,
            ["message_id"] = messageId,
            ["args"] = args
        };
        await socket.SendAsync(Encoding.UTF8.GetBytes(payload.ToJsonString()), WebSocketMessageType.Text, true, ct);

        if (command == "auth")
        {
            await ReadResultAsync(socket, messageId, ct);
        }
    }

    // Frames for other message ids (MA pushes player/queue events on the same socket) are skipped.
    // A response may arrive split across frames, each flagged partial:true except the last, so array
    // results are accumulated until the final frame.
    private static async Task<JsonNode?> ReadResultAsync(ClientWebSocket socket, string messageId, CancellationToken ct)
    {
        JsonArray? accumulated = null;

        while (true)
        {
            var frame = await ReceiveAsync(socket, ct)
                        ?? throw new MusicAssistantException("Music Assistant closed the connection before replying.");

            if (frame["message_id"]?.GetValue<string>() != messageId)
            {
                continue;
            }

            if (frame["error_code"] is not null)
            {
                var details = frame["details"]?.GetValue<string>() ?? frame["error_code"]!.ToJsonString();
                throw new MusicAssistantException($"Music Assistant rejected the request: {details}");
            }

            var result = frame["result"];
            var partial = frame["partial"]?.GetValue<bool>() == true;

            if (!partial && accumulated is null)
            {
                return result;
            }

            accumulated ??= [];
            if (result is JsonArray chunk)
            {
                foreach (var item in chunk.ToList())
                {
                    accumulated.Add(item?.DeepClone());
                }
            }

            if (!partial)
            {
                return accumulated;
            }
        }
    }

    private static async Task<JsonNode?> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        var builder = new StringBuilder();
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, ct);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            builder.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
            if (received.EndOfMessage)
            {
                return JsonNode.Parse(builder.ToString());
            }
        }
    }

    private static Uri WebSocketUri(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/');
        var scheme = normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" : "ws://";
        var authority = normalized
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase);
        return new Uri($"{scheme}{authority}/ws");
    }
}