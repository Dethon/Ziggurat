using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mcp.Hosting.Tunnel;

// A listener of this server's that the relay may open streams to, by tag. Every tunnelled server has
// its /mcp listener under "mcp" without saying so; a server with a second listener the proxy routes
// pages to names it here.
public sealed record TunnelListener(string Tag, int Port);

// The server's end of its tunnel: one outbound WebSocket to the relay, kept open for as long as the
// server runs and opened again with backoff whenever it drops. Every stream the relay opens becomes
// a TCP connection to one of this server's own listeners on loopback, carrying the relay's bytes as
// they are — so the credential gate, the call-token gate and the pages listener see the request
// exactly as if it had been dialled, and none of them knows a tunnel exists.
//
// The relay's control messages say who the tunnel is for and what the agent's keys are; they land
// in TunnelTrust, which the call-token gate checks against.
internal sealed class TunnelClient(
    TunnelSettings settings,
    TunnelTrust trust,
    TunnelLink link,
    IServiceProvider services,
    ILogger<TunnelClient> logger) : BackgroundService
{
    // A test that walks the backoff hands the client its clock under this key.
    internal const string ClockKey = "tunnel";

    private readonly TimeProvider _clock = services.GetKeyedService<TimeProvider>(ClockKey) ?? TimeProvider.System;

    // Read once: a file that is not a certificate fails the server's start naming the setting, rather
    // than every open failing one at a time.
    private readonly X509Certificate2? _hubAuthority = settings.HubCertificateAuthority.Length > 0
        ? X509Certificate2.CreateFromPem(File.ReadAllText(settings.HubCertificateAuthority))
        : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Its listeners' ports are only known once the server is listening, and a stream opened before
        // that would have nowhere to go.
        await StartedAsync(stoppingToken);
        var listeners = Listeners();
        var next = settings.ReconnectAfter;
        while (!stoppingToken.IsCancellationRequested)
        {
            var outcome = await OpenAsync(listeners, stoppingToken);
            var wait = outcome switch
            {
                Outcome.Admitted => settings.ReconnectAfter,
                Outcome.Refused => settings.ReconnectAtMost,
                _ => next
            };
            next = outcome == Outcome.Admitted
                ? Doubled(settings.ReconnectAfter)
                : Doubled(next);

            try
            {
                await Task.Delay(wait, _clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // The system's roots vouched for it, or the authority this server was told of did. Only the chain
    // is ever taken on the authority's word: a certificate for another name, or none, is refused. The
    // authority is a root, and the hub's certificate is issued under an intermediate it serves beside
    // it (Caddy's local CA does): what the hub sent is offered to the chain, never trusted by itself.
    private static bool IssuedForTheHub(
        X509Certificate? presented, X509Chain? sent, SslPolicyErrors errors, X509Certificate2 authority)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || presented is null)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.AddRange(
            sent?.ChainElements.Skip(1).Select(e => e.Certificate).ToArray() ?? []);
        using var certificate = new X509Certificate2(presented);
        return chain.Build(certificate);
    }

    private TimeSpan Doubled(TimeSpan wait) =>
        wait * 2 > settings.ReconnectAtMost ? settings.ReconnectAtMost : wait * 2;

    private async Task<Outcome> OpenAsync(IReadOnlyDictionary<string, int> listeners, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", SharedSecret.Header(settings.Credential));
        socket.Options.SetRequestHeader(TunnelProtocol.ServerHeader, settings.ServerName);
        socket.Options.KeepAliveInterval = settings.KeepAlive;
        socket.Options.KeepAliveTimeout = settings.KeepAliveTimeout;
        if (_hubAuthority is not null)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, sent, errors) =>
                IssuedForTheHub(certificate, sent, errors, _hubAuthority);
        }

        try
        {
            await socket.ConnectAsync(new Uri(settings.Hub), ct);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("The tunnel to {Hub} could not be opened: {Reason}", settings.Hub, ex.Message);
            return Outcome.Failed;
        }
        catch (OperationCanceledException)
        {
            return Outcome.Failed;
        }

        var admitted = false;
        TunnelMux? opened = null;
        await using var mux = new TunnelMux(
            socket,
            TunnelEnd.Server,
            (json, _) =>
            {
                if (Control(json))
                {
                    admitted = true;
                    // Back-calls go out only once the relay has said who this tunnel is for.
                    link.Admitted(opened!);
                }

                return Task.CompletedTask;
            },
            (stream, tag) => PipeAsync(stream, tag, listeners, ct));
        opened = mux;
        try
        {
            await mux.RunAsync(ct);
        }
        finally
        {
            link.Ended(mux);
        }

        if (ct.IsCancellationRequested)
        {
            return Outcome.Failed;
        }

        var reason = mux.CloseStatusDescription ?? "the connection dropped";
        if (mux.CloseStatus == WebSocketCloseStatus.PolicyViolation)
        {
            logger.LogError(
                "The relay {Verdict} this server's tunnel as {ServerName}: {Reason}",
                admitted ? "closed" : "refused", settings.ServerName, reason);
            return Outcome.Refused;
        }

        if (!admitted)
        {
            logger.LogWarning("The tunnel to {Hub} closed before the relay admitted it: {Reason}", settings.Hub, reason);
            return Outcome.Failed;
        }

        logger.LogWarning("The tunnel to {Hub} closed: {Reason}", settings.Hub, reason);
        return Outcome.Admitted;
    }

    // Whether this message admitted the tunnel. A message that does not read is logged and skipped:
    // it is the relay's word, and a relay newer than this server may say things it does not know.
    private bool Control(string json)
    {
        try
        {
            switch (JsonSerializer.Deserialize<TunnelControl>(json, TunnelProtocol.SerializerOptions))
            {
                case TunnelAdmitted admitted:
                    trust.Admit(admitted.TenantId, admitted.Audience, admitted.Keys);
                    logger.LogInformation(
                        "The relay admitted this server's tunnel as {ServerName} of household {TenantId}",
                        admitted.ServerName, admitted.TenantId);
                    return true;
                case TunnelKeys keys:
                    trust.Rotate(keys.Keys);
                    return false;
                default:
                    return false;
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The relay sent a control message this server does not read");
            return false;
        }
    }

    // One stream is one TCP connection to the listener its tag names, for as long as either end
    // keeps it open.
    private static async Task PipeAsync(
        TunnelStream stream, string tag, IReadOnlyDictionary<string, int> listeners, CancellationToken ct)
    {
        if (!listeners.TryGetValue(tag, out var port))
        {
            stream.Reset($"this server has no '{tag}' listener");
            return;
        }

        using var connection = new TcpClient();
        try
        {
            await connection.ConnectAsync(IPAddress.Loopback, port, ct);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            stream.Reset($"the '{tag}' listener did not answer");
            return;
        }

        await using (stream)
        await using (var network = connection.GetStream())
        {
            try
            {
                await Task.WhenAny(stream.CopyToAsync(network, ct), network.CopyToAsync(stream, ct));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }

    private async Task StartedAsync(CancellationToken ct)
    {
        var started = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted;
        if (started.IsCancellationRequested)
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var onStarted = started.Register(() => signal.TrySetResult());
        await using var onStopping = ct.Register(() => signal.TrySetCanceled(ct));
        await signal.Task;
    }

    // The /mcp listener is the first the server bound unless it named one; any other is named.
    private IReadOnlyDictionary<string, int> Listeners()
    {
        var named = services.GetServices<TunnelListener>().ToDictionary(l => l.Tag, l => l.Port);
        var bound = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        if (!named.ContainsKey(TunnelProtocol.McpTag) && bound.FirstOrDefault() is { } first)
        {
            named[TunnelProtocol.McpTag] = BindingAddress.Parse(first).Port;
        }

        return named;
    }

    private enum Outcome
    {
        Admitted,
        Refused,
        Failed
    }
}