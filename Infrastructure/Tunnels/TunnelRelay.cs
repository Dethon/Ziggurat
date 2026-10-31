using System.Net.WebSockets;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Clients.Accounts;
using Mcp.Hosting.Tunnel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Tunnels;

// The deployment's end of every tunnel. A household's server opens a WebSocket here presenting its
// credential and the name it claims; the accounts service is asked whose server that credential
// opens a tunnel for, on every open. A refused tunnel is closed with the reason, logged and reported
// on the errors feed; an admitted one is told who it is and the agent's keys, registered, and kept
// for as long as its connection lives.
//
// Nothing a server sends is believed about who it is: the household, the name and the synthetic
// address all come from the verified credential, and the server is told them.
public sealed class TunnelRelay(
    TunnelRegistry registry,
    TunnelCredentialClient accounts,
    SigningKeyStore keys,
    TunnelSettings settings,
    IMetricsPublisher metrics,
    TimeProvider clock,
    ILogger<TunnelRelay> logger,
    IHostApplicationLifetime? lifetime = null,
    BackCallGateway? backCalls = null)
{
    public const string RefusedErrorType = "TunnelRefused";

    // How long a refused or unverified tunnel is given to answer its close before it is dropped.
    private static readonly TimeSpan _closeGrace = TimeSpan.FromSeconds(5);

    public async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var presented = SharedSecret.Bearer(context.Request.Headers.Authorization.ToString()) ?? "";
        var claimed = context.Request.Headers[TunnelProtocol.ServerHeader].ToString();
        var ct = context.RequestAborted;
        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = settings.KeepAlive,
            KeepAliveTimeout = settings.KeepAliveTimeout
        });

        // A server-opened stream is a back-call, which the gateway takes once the tunnel is admitted
        // and only to its allowlist; with no gateway, each is reset.
        OpenTunnel? admitted = null;
        await using var mux = new TunnelMux(
            socket,
            TunnelEnd.Relay,
            (_, _) => Task.CompletedTask,
            backCalls is null ? null : (stream, tag) => backCalls.AcceptAsync(Volatile.Read(ref admitted), stream, tag));
        switch (await VerifyAsync(presented, claimed, ct))
        {
            case TunnelVerdict.Opens { Credential: var credential }:
                await RelayAsync(mux, credential, presented, tunnel => Volatile.Write(ref admitted, tunnel), ct);
                break;
            case TunnelVerdict.Refused { Reason: var reason }:
                Report(claimed, reason);
                await EndAsync(mux, WebSocketCloseStatus.PolicyViolation, reason.ToString());
                break;
            default:
                await EndAsync(mux, WebSocketCloseStatus.InternalServerError, TunnelProtocol.Unverified);
                break;
        }
    }

    private async Task RelayAsync(
        TunnelMux mux, TunnelCredentialVerified credential, string presented, Action<OpenTunnel> admit, CancellationToken ct)
    {
        await using var tunnel = new OpenTunnel(
            credential, presented, registry.NextGeneration(), clock.GetUtcNow(), mux, keys);
        admit(tunnel);
        await tunnel.SendAsync(tunnel.Admitted, ct);
        if (registry.Add(tunnel) is { } replaced)
        {
            await replaced.CloseAsync(WebSocketCloseStatus.PolicyViolation, TunnelProtocol.Replaced);
        }

        logger.LogInformation(
            "The tunnel of {ServerName} for household {TenantId} opened (generation {Generation})",
            tunnel.ServerName, tunnel.TenantId, tunnel.Generation);
        // A deploy's stop says so to every server, which comes back with backoff, rather than holding
        // the agent's shutdown open until its connections are cut.
        await using var stopping = (lifetime?.ApplicationStopping ?? CancellationToken.None).Register(() =>
            _ = tunnel.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, TunnelProtocol.Restarting));
        try
        {
            await mux.RunAsync(ct);
        }
        finally
        {
            var reason = mux.CloseStatusDescription is { Length: > 0 } description ? description : "the connection dropped";
            registry.Remove(tunnel, reason);
            logger.LogInformation(
                "The tunnel of {ServerName} for household {TenantId} closed (generation {Generation}): {Reason}",
                tunnel.ServerName, tunnel.TenantId, tunnel.Generation, reason);
        }
    }

    // Null when the accounts service could not say: not the credential's fault, so the server is
    // told to try again rather than that it was refused.
    private async Task<TunnelVerdict?> VerifyAsync(string presented, string claimed, CancellationToken ct)
    {
        if (presented.Length == 0)
        {
            return new TunnelVerdict.Refused(TunnelCredentialRefusal.Unknown);
        }

        try
        {
            return await accounts.VerifyAsync(presented, claimed, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "A tunnel claiming {ServerName} could not be verified: the accounts service did not answer", claimed);
            return null;
        }
    }

    // The credential itself never reaches a log or the feed: only the name it was presented for.
    private void Report(string claimed, TunnelCredentialRefusal reason)
    {
        logger.LogWarning("Refused a tunnel claiming {ServerName}: its credential is {Reason}", claimed, reason);
        metrics.Publish(new ErrorEvent
        {
            Service = "agent",
            ErrorType = RefusedErrorType,
            Message = $"A tunnel claiming server '{claimed}' was refused: {reason}"
        });
    }

    private static async Task EndAsync(TunnelMux mux, WebSocketCloseStatus status, string reason)
    {
        await mux.CloseAsync(status, reason, CancellationToken.None);
        using var grace = new CancellationTokenSource(_closeGrace);
        await mux.RunAsync(grace.Token);
    }
}