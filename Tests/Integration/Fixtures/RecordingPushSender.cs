using System.Net;
using System.Text.Json.Nodes;
using Infrastructure.Clients.Push;

namespace Tests.Integration.Fixtures;

// A push service that keeps what it was handed, as plain JSON before it would be encrypted, and
// answers 410 for the endpoints a test says are gone.
public sealed class RecordingPushSender : IPushMessageSender
{
    private readonly Lock _lock = new();
    private readonly List<(string Endpoint, JsonNode Payload)> _sent = [];

    public HashSet<string> Gone { get; } = [];

    public IReadOnlyList<(string Endpoint, JsonNode Payload)> Sent
    {
        get
        {
            lock (_lock)
            {
                return [.. _sent];
            }
        }
    }

    public Task SendAsync(string endpoint, string p256dh, string auth, string payload, CancellationToken ct = default)
    {
        if (Gone.Contains(endpoint))
        {
            throw new WebPushSendException("Gone", HttpStatusCode.Gone);
        }

        lock (_lock)
        {
            _sent.Add((endpoint, JsonNode.Parse(payload)!));
        }

        return Task.CompletedTask;
    }
}