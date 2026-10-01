using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Agent.App;
using Domain.Contracts;
using Domain.Tools.FileSystem.Bridge;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Infrastructure.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Tests.Integration.Fixtures;

namespace Tests.E2E.Fixtures;

// The sandbox server as it ships: its own image, built from its own Dockerfile, reached over the
// wire. No browser, so this fixture does not extend E2EFixtureBase — there is nothing here for
// Chromium to do, and starting one per collection was the cost that capped how far the E2E suite
// could be split.
//
// It is the only layer where the image is real. The in-process sandbox fixture constructs the
// server against a temporary root and cannot see the Dockerfile at all, so the mount-point alias
// baked into the image is invisible to every other test.
public sealed class SandboxE2EFixture : IAsyncLifetime
{
    private IContainer? _sandbox;
    private WebApplication? _bridgeHost;

    // The bridge the container's daemons call: a test mints a call on it over a registry of its own
    // and hands the token to fs_exec on the call's `_meta`, as the agent's exec tool does.
    public VfsBridge Bridge { get; } = new(TimeProvider.System);

    public string McpEndpoint { get; private set; } = "";

    // The host side of the container's persistent workspace, so a test can look at the file the
    // container wrote where the volume — not the container's own layer — keeps it.
    public string WorkspaceOnHost { get; private set; } = "";

    // Docker missing is a machine that cannot run this test, not a failing one.
    public bool Available => _sandbox is not null;

    private static readonly TimeSpan _containerStartupTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _imageBuildTimeout = TimeSpan.FromMinutes(20);

    public async Task InitializeAsync()
    {
        // The subject is a container running as a user who owns its home and nothing else, so a
        // host that cannot produce that pairing has nothing to say here rather than something
        // weaker: as root every write succeeds and the permissions fact inverts silently.
        if (!OperatingSystem.IsLinux() || geteuid() == 0 || !await DockerIsRunningAsync())
        {
            return;
        }

        var name = GetType().Name;
        var solutionRoot = TestHelpers.FindSolutionRoot();
        await E2EPhase.RunAsync(name, "image build", _imageBuildTimeout, async ct =>
        {
            // The leaf image is FROM base-sdk:latest, so this must complete first.
            await TestHelpers.EnsureBaseSdkImageAsync(solutionRoot, ct);
            await TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.McpSandbox, ct);
        });

        WorkspaceOnHost = Path.Combine(Path.GetTempPath(), $"mcp-sandbox-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(WorkspaceOnHost);

        // The agent's bridge endpoint, hosted here over whatever registry a test mints a call
        // against: the container reaches it through the host gateway, as compose's sandbox reaches
        // the agent by name.
        var bridgePort = TestPort.GetAvailable();
        var bridgeHost = WebApplication.CreateBuilder();
        bridgeHost.WebHost.UseKestrel(options => options.Listen(IPAddress.Any, bridgePort));
        bridgeHost.Services.AddSingleton<IVfsBridge>(Bridge);
        var bridgeApp = bridgeHost.Build();
        bridgeApp.MapVfsBridge();
        await bridgeApp.StartAsync();
        _bridgeHost = bridgeApp;

        await E2EPhase.RunAsync(name, "container startup", _containerStartupTimeout, async ct =>
        {
            // Compose's pairing, reproduced: commands as an unprivileged user and a mount it owns at
            // the home directory. Both halves are what make a permissions fact real here — the container
            // root stays root-owned and unwritable, and the workspace is writable because the host
            // directory belongs to whoever is running the test. The in-process fixture builds the
            // server against a temporary root the test user owns outright, so it cannot tell the
            // two apart, which is how a landing that never landed anything shipped green.
            _sandbox = TestContainers.Container(E2EImages.McpSandbox.ImageName, "mcp-sandbox")
                .WithPortBinding(8080, true)
                .WithBindMount(WorkspaceOnHost, ContainerWorkspace, AccessMode.ReadWrite)
                .AsCompose()
                // A deployment secret in the server's environment, which no command may see.
                .WithEnvironment(PlantedSecretName, PlantedSecret)
                .WithExtraHost("host.docker.internal", "host-gateway")
                .WithEnvironment("VFSBRIDGEURL", $"http://host.docker.internal:{bridgePort}{VfsBridgeApi.Route}")
                // The deployment secret its /mcp asks for, as compose hands it every MCP server.
                .WithEnvironment("MCP__SHAREDSECRET", McpTestSecret.Value)
                // The published port answers before Kestrel has bound anything — Docker's proxy
                // accepts the connection and the app then resets it — so a TCP check returns while
                // the server is still starting and every test fails on a reset. `GET /mcp` is the
                // cheapest request the transport really serves: 405, because the endpoint is POST.
                // It presents the secret, or the gate in front of /mcp answers 401 before the
                // transport is asked anything.
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                    .ForPort(8080)
                    .ForPath("/mcp")
                    .WithHeaders(McpTestSecret.Headers)
                    .ForStatusCode(HttpStatusCode.MethodNotAllowed)))
                .Build();
            await _sandbox.StartAsync(ct);
        });

        McpEndpoint = $"http://{_sandbox!.Hostname}:{_sandbox.GetMappedPublicPort(8080)}/mcp";
    }

    // Stop and start the container the tests share, so a claim about surviving a restart is bought
    // with a restart rather than with a look at the host side of the volume. The port is remapped
    // on the way back up, so the endpoint is recomputed; the classes in this collection run one
    // after another, so nothing is mid-request while it happens.
    public async Task RestartAsync(CancellationToken ct)
    {
        await _sandbox!.StopAsync(ct);
        await _sandbox.StartAsync(ct);
        McpEndpoint = $"http://{_sandbox.Hostname}:{_sandbox.GetMappedPublicPort(8080)}/mcp";
    }

    public async Task<McpClient> ConnectAsync(CancellationToken ct) =>
        await McpClient.CreateAsync(
            McpTestSecret.Transport(McpEndpoint),
            cancellationToken: ct);

    public async Task DisposeAsync()
    {
        if (_sandbox is not null)
        {
            await _sandbox.DisposeAsync();
        }

        if (_bridgeHost is not null)
        {
            await _bridgeHost.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(WorkspaceOnHost))
            {
                Directory.Delete(WorkspaceOnHost, true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not a failing test.
        }
    }

    // The sandbox's HomeDir setting, which its appsettings ships and compose gives a volume. The
    // published workspace is read off the mount rather than written here; this is only where the
    // host directory is attached.
    public const string ContainerWorkspace = "/home/sandbox_user";

    public static uint Uid => geteuid();

    public static uint Gid => getegid();

    public const string PlantedSecretName = "OPENROUTER__APIKEY";
    public const string PlantedSecret = "sk-planted-never-shown";

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern uint getegid();

    private static async Task<bool> DockerIsRunningAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "version --format {{.Server.Os}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

// One container for every sandbox E2E class. A class fixture each would start a second container
// for a subject that is per-image, not per-class.
[CollectionDefinition(SandboxE2ECollection.Name)]
public class SandboxE2ECollection : ICollectionFixture<SandboxE2EFixture>
{
    public const string Name = "SandboxE2E";
}