using System.Net;
using Domain.DTOs.Tunnel;
using Mcp.Hosting.Tunnel;
using Yarp.ReverseProxy.Forwarder;

namespace LemonadeForwarder;

// A household's Lemonade forwarder: its tunnel to the deployment under the name lemonade, and one
// listener, under the chat tag, that passes every request the relay opens to the household's box on
// its own network and streams the answer back. It serves no MCP and makes no back-call. The box is
// its one destination whatever a request names, and the box's key, where it has one, is added here,
// so it never leaves the house.
public static class ForwarderApp
{
    // Streamed both ways and never timed out by us: a reply is read while the box makes it, and a slow
    // model is the box's to bound.
    private static readonly ForwarderRequestConfig _config = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(30)
    };

    public static WebApplication Build(ForwarderSettings settings, WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(settings.Box.Url) || !Uri.TryCreate(settings.Box.Url, UriKind.Absolute, out var box)
            || box.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "Box:Url (environment variable BOX__URL) must be the box's http(s) API address, such as " +
                "http://192.168.1.20:13305/api/v1");
        }

        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, settings.Listener.Port));
        builder.Services
            .AddHttpForwarder()
            .AddTunnelListener(TunnelProtocol.ChatTag, settings.Listener.Port)
            .AddTunnelledHost(settings.Mcp.Tunnel);

        var app = builder.Build();
        var invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ActivityHeadersPropagator = null
        });
        var destination = settings.Box.Url.TrimEnd('/');
        var transformer = new ToTheBox(settings.Box.ApiKey);
        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        app.Map("/{**path}", async (HttpContext http) =>
        {
            var error = await forwarder.SendAsync(http, destination, invoker, _config, transformer);
            if (error != ForwarderError.None && !http.Response.HasStarted)
            {
                http.Response.StatusCode = StatusCodes.Status502BadGateway;
                await http.Response.WriteAsync($"the Lemonade box did not answer: {error}", http.RequestAborted);
            }
        });
        return app;
    }

    private sealed class ToTheBox(string apiKey) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(
                destinationPrefix, httpContext.Request.Path, httpContext.Request.QueryString);
            // The box's own name, never one a request carried.
            proxyRequest.Headers.Host = null;
            proxyRequest.Headers.Remove("Authorization");
            if (apiKey.Length > 0)
            {
                proxyRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }
        }
    }
}