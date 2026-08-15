using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Domain.DTOs;

namespace McpServerOutpost.Registration;

// The three calls, over HTTP, at the agent's own API. The shared secret rides every one of them:
// anyone who can reach that port could otherwise attach a machine to somebody else's assistant.
internal sealed class HttpOutpostAnnouncer(HttpClient client, string sharedSecret) : IOutpostAnnouncer
{
    private const string Route = "api/outposts";

    public async Task<bool> RegisterAsync(OutpostRegistration registration, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, Route);
        request.Content = JsonContent.Create(registration);
        using var response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // A not-found is the hub saying it has forgotten this machine, which is a different thing from
    // the hub being unreachable: one is answered by announcing again, the other by trying again.
    public async Task<KeepAliveAnswer> KeepAliveAsync(string name, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Put, $"{Route}/{Uri.EscapeDataString(name)}");
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return response.StatusCode is HttpStatusCode.NotFound
                ? KeepAliveAnswer.Lapsed
                : KeepAliveAnswer.Unreachable;
        }

        // A body that is absent or unreadable reads as "not yet known", which is what the machine
        // does about it anyway: the verdict is feedback, and a keepalive that refreshed the
        // registration did its job whether or not the answer could be understood.
        var body = await response.Content.ReadFromJsonAsync<VerdictBody>(ct);
        return new KeepAliveAnswer(KeepAliveOutcome.Refreshed, body?.Verdict ?? OutpostVerdict.Unknown);
    }

    public async Task DeregisterAsync(string name, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Delete, $"{Route}/{Uri.EscapeDataString(name)}");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed record VerdictBody(OutpostVerdict Verdict);

    private HttpRequestMessage Authorized(HttpMethod method, string route) =>
        new(method, route) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", sharedSecret) } };
}