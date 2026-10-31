using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Mcp.Hosting.Tunnel;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tunnels;

// Where the relay sends every stream a household's server opens: its back-calls. A Kestrel of its own,
// listening on nothing but those streams (BackCallTransport), reads each request and lets it through
// only if its tag names a back-call its server may make and its path is one of that back-call's
// (BackCallTargets) — forwarded to the deployment service behind it with who asked: the tunnel's
// household and server name in BackCallProtocol's headers, set over any the server sent. A stream
// with any other tag, and a request to any other path or from a server its tag is not for, is
// refused, logged and reported on the errors feed.
//
// Its own Kestrel rather than the agent's: the agent's listens where its configuration says, and a
// listener added in code would replace that configuration rather than join it.
public sealed class BackCallGateway(
    BackCallTargets targets,
    IMetricsPublisher metrics,
    IServiceProvider services,
    ILoggerFactory loggers) : IHostedService, IDisposable
{
    public const string RefusedErrorType = "BackCallRefused";

    // A metric event is a few hundred bytes; a server sending more is not publishing one.
    private const int MaxMetricEvent = 64 * 1024;

    private static readonly JsonSerializerOptions _metricJson = new(JsonSerializerDefaults.Web);

    // A request's own framing and the connection's, which Kestrel and the client to the target each
    // speak for themselves; and Host, which names the target's own.
    private static readonly HashSet<string> _hopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host",
        "Proxy-Authorization", "Proxy-Authenticate"
    };

    private readonly ILogger _logger = loggers.CreateLogger<BackCallGateway>();
    private readonly BackCallTransport _transport = new();

    // A target's answer streams as it comes: a spoken reply is read while Lemonade is still making it.
    private readonly HttpClient _forward = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private KestrelServer? _server;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = new KestrelServerOptions { ApplicationServices = services, AddServerHeader = false };
        // The targets bound their own bodies (the bridge its file ceiling); this only passes them on.
        options.Limits.MaxRequestBodySize = null;
        options.Listen(new BackCallEndPoint(), listen => listen.Protocols = HttpProtocols.Http1);
        _server = new KestrelServer(Options.Create(options), _transport, loggers);
        await _server.StartAsync(new Application(HandleAsync), cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _transport.Complete();
        if (_server is not null)
        {
            await _server.StopAsync(cancellationToken);
        }
    }

    // Synchronous, so a container disposed either way can let it go.
    public void Dispose()
    {
        _transport.Complete();
        _server?.Dispose();
        _forward.Dispose();
    }

    // The relay's answer to a stream its tunnel's server opened. Before the tunnel is admitted there
    // is nobody to say asked, so nothing is taken.
    internal Task AcceptAsync(OpenTunnel? tunnel, TunnelStream stream, string tag)
    {
        if (tunnel is null)
        {
            stream.Reset("the tunnel is not admitted yet");
        }
        else if (!targets.Knows(tag))
        {
            Refuse(tunnel, $"a '{tag}' stream, which is no back-call");
            stream.Reset($"'{tag}' is no back-call");
        }
        else if (!_transport.Offer(stream, new BackCallOrigin(tunnel, tag)))
        {
            stream.Reset("the agent is stopping");
        }

        return Task.CompletedTask;
    }

    private async Task HandleAsync(HttpContext http)
    {
        var origin = http.Features.Get<BackCallOrigin>()
                     ?? throw new InvalidOperationException("A back-call arrived on no tunnel.");
        var path = http.Request.Path.Value ?? "";
        if (!HttpMethods.IsPost(http.Request.Method) || !targets.Allows(origin.Tag, origin.Tunnel.ServerName, path))
        {
            Refuse(origin.Tunnel, $"{http.Request.Method} {path} on its '{origin.Tag}' back-call");
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsync($"{http.Request.Method} {path} is not a '{origin.Tag}' back-call");
            return;
        }

        if (origin.Tag == BackCallProtocol.Metrics)
        {
            await PublishAsync(http, origin.Tunnel);
            return;
        }

        await ForwardAsync(http, origin);
    }

    // The relay publishes it onto the feed itself, as the server's own: a household's server reaches
    // no deployment database. Origin is the relay's to say, never the event's.
    private async Task PublishAsync(HttpContext http, OpenTunnel tunnel)
    {
        MetricEvent? published = null;
        if (http.Request.ContentLength is null or <= MaxMetricEvent)
        {
            try
            {
                using var body = new MemoryStream();
                await http.Request.Body.CopyToAsync(body, http.RequestAborted);
                published = body.Length <= MaxMetricEvent
                    ? JsonSerializer.Deserialize<MetricEvent>(body.ToArray(), _metricJson)
                    : null;
            }
            catch (JsonException)
            {
            }
        }

        if (published is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        metrics.Publish(published with { Origin = tunnel.Address.Name });
        http.Response.StatusCode = StatusCodes.Status202Accepted;
    }

    private async Task ForwardAsync(HttpContext http, BackCallOrigin origin)
    {
        var tunnel = origin.Tunnel;
        using var request = new HttpRequestMessage(
            new HttpMethod(http.Request.Method),
            new Uri(targets.BaseOf(origin.Tag, OwnAddress), http.Request.Path.Value!.TrimStart('/') + http.Request.QueryString));
        if (http.Request.ContentLength is > 0 || http.Request.Headers.TransferEncoding.Count > 0)
        {
            request.Content = new StreamContent(http.Request.Body);
        }

        foreach (var (name, values) in http.Request.Headers)
        {
            if (_hopByHop.Contains(name) || name.StartsWith(BackCallProtocol.HeaderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }

        request.Headers.TryAddWithoutValidation(BackCallProtocol.HouseholdHeader, tunnel.TenantId);
        request.Headers.TryAddWithoutValidation(BackCallProtocol.ServerHeader, tunnel.ServerName);
        if (targets.PresentsCredential(origin.Tag))
        {
            // The accounts service believes no header about who asks: it is shown the credential the
            // tunnel was opened with and reads the household and the server off it itself.
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(tunnel.Presented));
        }

        HttpResponseMessage response;
        try
        {
            response = await _forward.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "The '{Tag}' back-call of {ServerName} for household {TenantId} found its target unreachable: {Reason}",
                origin.Tag, tunnel.ServerName, tunnel.TenantId, ex.Message);
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (response)
        {
            http.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (!_hopByHop.Contains(name))
                {
                    http.Response.Headers[name] = values.ToArray();
                }
            }

            await using var body = await response.Content.ReadAsStreamAsync(http.RequestAborted);
            await body.CopyToAsync(http.Response.Body, http.RequestAborted);
        }
    }

    // Who refused what is said with the tunnel's identity, never its credential.
    private void Refuse(OpenTunnel tunnel, string what)
    {
        _logger.LogWarning(
            "Refused a back-call of {ServerName} for household {TenantId}: {What}", tunnel.ServerName, tunnel.TenantId, what);
        metrics.Publish(new ErrorEvent
        {
            Service = "agent",
            ErrorType = RefusedErrorType,
            Message = $"Server '{tunnel.ServerName}' of household {tunnel.TenantId} was refused {what}"
        });
    }

    // The agent's own routes (the bridge, topic minting) are asked where the agent itself listens, read
    // off its server once it is: one address fewer to configure, and the same in every stack.
    private Uri OwnAddress()
    {
        var bound = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                    ?? throw new InvalidOperationException("The agent is not listening anywhere its own back-calls could reach.");
        // On loopback whatever the agent bound: every stack binds it either everywhere or there.
        var address = BindingAddress.Parse(bound);
        return new Uri($"{address.Scheme}://127.0.0.1:{address.Port}/");
    }

    private sealed class Application(RequestDelegate handle) : IHttpApplication<HttpContext>
    {
        public HttpContext CreateContext(IFeatureCollection contextFeatures) => new DefaultHttpContext(contextFeatures);

        public Task ProcessRequestAsync(HttpContext context) => handle(context);

        public void DisposeContext(HttpContext context, Exception? exception)
        {
        }
    }
}