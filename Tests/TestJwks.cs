using System.Net;
using Domain.Security.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tests;

// The test agent's published keys, served the way an issuer serves its own, so a server a test boots
// fetches the keys it checks a call token with over HTTP as it does in a deployment. One listener for
// the whole run, started the first time a fixture asks: every server the suite boots verifies against
// the same fixed keys (TestSigningKeys), so there is nothing per test to serve.
//
// On every interface, so a container a test starts reaches it too, through host.docker.internal
// (host-gateway) — the sandbox's containers are dialled by the test process itself, with no agent of
// their own to publish anything.
internal static class TestJwks
{
    private const string AgentPath = "/agent";

    private static readonly Lazy<int> _port = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string AgentUrl => $"http://localhost:{_port.Value}{AgentPath}";

    // The same keys as seen from a container that maps host.docker.internal to the host gateway.
    public static string AgentUrlFromContainers => $"http://host.docker.internal:{_port.Value}{AgentPath}";

    private static int Start()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Any, 0));
        var app = builder.Build();
        app.MapGet(AgentPath, () => Published(TestSigningKeys.Agent));
        app.StartAsync().GetAwaiter().GetResult();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        return new Uri(address.Replace("0.0.0.0", "localhost", StringComparison.Ordinal)).Port;
    }

    private static IResult Published(string privateKey) =>
        Results.Content(TestSigningKeys.Store(privateKey).PublishedKeys.ToJson(), "application/json");
}