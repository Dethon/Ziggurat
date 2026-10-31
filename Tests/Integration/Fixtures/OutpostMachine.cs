using System.Net;
using Domain.DTOs.Tunnel;
using McpServerOutpost.Modules;
using McpServerOutpost.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Tests.Integration.Fixtures;

// One machine running the outpost, served by the binary's own ConfigureMcp and listening where the
// binary listens — loopback, on a port nothing dials — so the tunnel it opens by itself is the only
// way to reach it, and what the agent reads off it (the machine address, the jail, the shell) is what
// a real outpost publishes.
internal sealed class OutpostMachine : IAsyncDisposable
{
    private readonly WebApplication _app;

    private OutpostMachine(WebApplication app, string workingDirectory)
    {
        _app = app;
        WorkingDirectory = workingDirectory;
    }

    public string WorkingDirectory { get; }

    public static async Task<OutpostMachine> StartAsync(
        string hub, string credential, string name, bool jailed = false, bool exec = false)
    {
        var workingDirectory = Directory.CreateTempSubdirectory($"outpost-{name}-").FullName;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.ConfigureMcp(new OutpostSettings
        {
            Name = name,
            WorkingDirectory = workingDirectory,
            Jailed = jailed,
            Exec = exec,
            Hub = hub,
            Credential = credential,
            // Quick to come back, so a test waits on the reconnect rather than the schedule.
            TunnelPolicy = new TunnelSettings
            {
                ReconnectAfter = TimeSpan.FromMilliseconds(50),
                ReconnectAtMost = TimeSpan.FromMilliseconds(200)
            }
        });
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        return new OutpostMachine(app, workingDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Directory.Delete(WorkingDirectory, recursive: true);
    }
}