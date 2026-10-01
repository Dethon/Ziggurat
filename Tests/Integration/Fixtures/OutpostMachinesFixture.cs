using System.Net;
using McpServerOutpost.Modules;
using McpServerOutpost.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

// Machines that registered themselves, each served by the outpost's own ConfigModule, so what
// discovery reads off them — the machine address included — is what a real outpost publishes rather
// than a stand-in's idea of it. No hub and no gate: registration is the stub registry's business in
// the tests that use these, and who may dial is OutpostSecretDialTests'.
//
// Two of them answer to one name in different case. That is the collision the hub can actually
// hold — its registrations are keyed by the exact name, while the agent's mount points are not — and
// the one a machine can now lose, since no mount of the deployment's own shares its address.
public sealed class OutpostMachinesFixture : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("outpost-machines-").FullName;
    private readonly List<IHost> _hosts = [];

    public string Laptop { get; private set; } = null!;

    // Another machine whose operator also called it laptop.
    public string Twin { get; private set; } = null!;

    // A machine named like the deployment's own vault.
    public string Vault { get; private set; } = null!;

    public const string TwinName = "LAPTOP";

    public async Task InitializeAsync()
    {
        Laptop = await StartAsync("laptop");
        Twin = await StartAsync(TwinName);
        Vault = await StartAsync("vault");
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_hosts.Select(h => h.StopAsync()));
        _hosts.ForEach(h => h.Dispose());
        Directory.Delete(_root, recursive: true);
    }

    private async Task<string> StartAsync(string name)
    {
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N")[..4])).FullName;
        var port = TestPort.GetAvailable();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new OutpostSettings { Name = name, WorkingDirectory = workingDirectory, Port = port });

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        _hosts.Add(app);

        return $"http://localhost:{port}/mcp";
    }
}