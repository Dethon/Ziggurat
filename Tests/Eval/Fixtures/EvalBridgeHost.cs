using System.Net;
using Agent.App;
using Domain.Contracts;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tests.Integration.Fixtures;

namespace Tests.Eval.Fixtures;

// The agent's exec bridge endpoint, hosted for a sandbox container to call. On every interface,
// because the caller is the container: it reaches the host through the gateway, as compose's
// sandbox reaches the agent by name — the url returned is spelled the container's way.
public static class EvalBridgeHost
{
    public static async Task<(string Url, IHost Host, int Port)> StartAsync(VfsBridge bridge)
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Any, port));
        builder.Services.AddSingleton<IVfsBridge>(bridge);
        var app = builder.Build();
        app.MapVfsBridge();
        await app.StartAsync();

        return ($"http://host.docker.internal:{port}{VfsBridgeApi.Route}", app, port);
    }
}