using System.Net;
using System.Runtime.InteropServices;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Tests.E2E.Fixtures;

namespace Tests.Eval.Fixtures;

// The sandbox, as the disposable container it is in production. Hosting it in process would run
// model-authored shell on whoever is running the suite — which is why the exec claims sat
// unwitnessed — so isolation is made the fixture's property instead: the real image, a throwaway
// workspace, one container per run, all gone with the stack. The image build is once per machine
// and cached; the per-run cost is a container start.
public sealed class EvalSandbox : IAsyncDisposable
{
    // Built once per test process, however many runs start a sandbox: the staleness check inside
    // EnsureImageAsync is cheap, but two runs racing the first build would both pay it.
    private static readonly SemaphoreSlim _imageGate = new(1, 1);
    private static bool _imageEnsured;

    private static readonly TimeSpan _imageBuildTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan _startTimeout = TimeSpan.FromMinutes(2);

    private IContainer _container = null!;

    public string WorkspaceOnHost { get; private set; } = "";

    public string Endpoint { get; private set; } = "";

    public static async Task<EvalSandbox> StartAsync()
    {
        await EnsureImageAsync();

        var sandbox = new EvalSandbox
        {
            WorkspaceOnHost = Path.Combine(Path.GetTempPath(), $"eval-sandbox-{Guid.NewGuid():N}")
        };
        Directory.CreateDirectory(sandbox.WorkspaceOnHost);

        // Compose's pairing, the same way SandboxE2EFixture reproduces it: an unprivileged user
        // and a home-directory mount it owns. The wait strategy is a GET against the POST-only
        // MCP endpoint, because Docker's proxy accepts TCP before Kestrel has bound anything.
        var builder = TestContainers.Container(E2EImages.McpSandbox.ImageName, "eval-sandbox")
            .WithPortBinding(8080, true)
            .WithBindMount(sandbox.WorkspaceOnHost, "/home/sandbox_user", AccessMode.ReadWrite)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(8080)
                .ForPath("/mcp")
                .ForStatusCode(HttpStatusCode.MethodNotAllowed)));

        if (OperatingSystem.IsLinux())
        {
            builder = builder.WithCreateParameterModifier(
                parameters => parameters.User = $"{geteuid()}:{getegid()}");
        }

        sandbox._container = builder.Build();
        using var startup = new CancellationTokenSource(_startTimeout);
        await sandbox._container.StartAsync(startup.Token);

        sandbox.Endpoint =
            $"http://{sandbox._container.Hostname}:{sandbox._container.GetMappedPublicPort(8080)}/mcp";
        return sandbox;
    }

    private static async Task EnsureImageAsync()
    {
        await _imageGate.WaitAsync();
        try
        {
            if (_imageEnsured)
            {
                return;
            }

            using var build = new CancellationTokenSource(_imageBuildTimeout);
            var solutionRoot = Harness.RepositoryRoot.Path;
            // The leaf image is FROM base-sdk:latest, so that one must exist first.
            await TestHelpers.EnsureBaseSdkImageAsync(solutionRoot, build.Token);
            await TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.McpSandbox, build.Token);
            _imageEnsured = true;
        }
        finally
        {
            _imageGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();

        try
        {
            if (Directory.Exists(WorkspaceOnHost))
            {
                Directory.Delete(WorkspaceOnHost, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not a failing run.
        }
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc")]
    private static extern uint getegid();
}