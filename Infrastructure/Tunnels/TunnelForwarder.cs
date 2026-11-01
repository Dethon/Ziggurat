using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace Infrastructure.Tunnels;

// Forwards one HTTP exchange that arrived at the agent down a household server's tunnel, to the
// listener a tag names, and its answer back — a WebSocket upgraded inside it included, whose bytes
// then run both ways for as long as either end keeps it. Every connection it makes is a stream of
// that tunnel (OpenTunnel.Forwarding), so nothing it sends goes onto the network.
//
// YARP's forwarder rather than a pipe of our own: an exchange is HTTP/1.1 between two Kestrels, and
// forwarding one well — hop-by-hop headers, a body streamed rather than buffered, a client that goes
// away mid-response, a 101 handed over to an opaque duplex stream — is what that library is.
//
// The request goes on as the caller sent it, Host and the proxy's X-Forwarded-* included: a page's
// origin check compares the browser's Origin with the Host it asked for, and its cookie is Secure
// by the X-Forwarded-Proto the proxy set. Only the path is the caller's to choose, and the headers
// it withholds.
public sealed class TunnelForwarder(IHttpForwarder forwarder, TunnelRegistry registry, ILogger<TunnelForwarder> logger)
{
    // A live view a person is reading sends nothing while its page stands still, and a quiet view is
    // not a dead one: as long as the longest a view may last (web search's Handoff:ViewMinutes).
    // The tunnel's own end, or the browser's, still ends it at once.
    private static readonly ForwarderRequestConfig _config = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(30),
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact
    };

    // To the home household's server of that name (TunnelRegistry.FindHome). With none open, or one
    // whose tunnel closed before its answer began, `offline` answers the exchange with the reason.
    public Task ForwardHomeAsync(
        HttpContext context,
        string serverName,
        string tag,
        PathString path,
        IReadOnlyCollection<string> withheld,
        Func<HttpContext, string, Task> offline) =>
        ForwardToAsync(context, registry.FindHome(serverName), serverName, tag, path, withheld, offline);

    // To one household's server of that name, and never another household's of the same name.
    public Task ForwardAsync(
        HttpContext context,
        string tenantId,
        string serverName,
        string tag,
        PathString path,
        IReadOnlyCollection<string> withheld,
        Func<HttpContext, string, Task> offline) =>
        ForwardToAsync(context, registry.Find(tenantId, serverName), serverName, tag, path, withheld, offline);

    private async Task ForwardToAsync(
        HttpContext context,
        OpenTunnel? found,
        string serverName,
        string tag,
        PathString path,
        IReadOnlyCollection<string> withheld,
        Func<HttpContext, string, Task> offline)
    {
        if (found is not { } tunnel)
        {
            await offline(context, $"{serverName} is offline: its tunnel is not open");
        }
        else if (!await ForwardAsync(context, tunnel, tag, path, withheld) && !context.Response.HasStarted)
        {
            await offline(context, $"{serverName} went offline: its tunnel closed");
        }
    }

    // False when the exchange could not be forwarded; the caller answers it if nothing was sent yet.
    private async Task<bool> ForwardAsync(
        HttpContext context, OpenTunnel tunnel, string tag, PathString path, IReadOnlyCollection<string> withheld)
    {
        var error = await forwarder.SendAsync(
            context, $"http://{tunnel.Address.Name}", tunnel.Forwarding(tag), _config, new Passing(path, withheld));
        if (error == ForwarderError.None)
        {
            return true;
        }

        logger.LogWarning(
            context.Features.Get<IForwarderErrorFeature>()?.Exception,
            "An exchange for {ServerName} of household {TenantId} was not forwarded down its tunnel: {Error}",
            tunnel.ServerName, tunnel.TenantId, error);
        return false;
    }

    private sealed class Passing(PathString path, IReadOnlyCollection<string> withheld) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(
                destinationPrefix, path, httpContext.Request.QueryString);
            foreach (var header in withheld)
            {
                proxyRequest.Headers.Remove(header);
            }
        }
    }
}