using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using ModelContextProtocol.Client;

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

    public string McpEndpoint { get; private set; } = "";

    // Docker missing is a machine that cannot run this test, not a failing one.
    public bool Available => _sandbox is not null;

    private static readonly TimeSpan _containerStartupTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _imageBuildTimeout = TimeSpan.FromMinutes(20);

    public async Task InitializeAsync()
    {
        if (!await DockerIsRunningAsync())
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

        await E2EPhase.RunAsync(name, "container startup", _containerStartupTimeout, async ct =>
        {
            // The image's own user, not compose's unprivileged one. Compose pairs that user with a
            // volume it owns; without the volume the container would come up with no writable home.
            // Nothing here writes — the alias is a link every user can read — so the difference
            // cannot reach what is being asserted.
            _sandbox = TestContainers.Container(E2EImages.McpSandbox.ImageName, "mcp-sandbox")
                .WithPortBinding(8080, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(8080))
                .Build();
            await _sandbox.StartAsync(ct);
        });

        McpEndpoint = $"http://{_sandbox!.Hostname}:{_sandbox.GetMappedPublicPort(8080)}/mcp";
    }

    public async Task<McpClient> ConnectAsync(CancellationToken ct) =>
        await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(McpEndpoint) }),
            cancellationToken: ct);

    public async Task DisposeAsync()
    {
        if (_sandbox is not null)
        {
            await _sandbox.DisposeAsync();
        }
    }

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