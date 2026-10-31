using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Domain.Security;
using Domain.Security.Signing;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Infrastructure.Agents.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Integration.Fixtures;
using Tests.Integration.Tunnels;
using Tests.Unit;
using Tests.Unit.Domain.Tools.FileSystem.Bridge;

namespace Tests.E2E.Sandbox;

// The sandbox as a household's server: its own image, told a hub, a credential, its name and the
// loopback port its bridge back-call goes out from, and nothing else — no deployment secret, no
// address of the agent's. The agent reaches its exec through the tunnel, and the command's FUSE
// daemon reaches the agent's bridge back through the same tunnel, so a command reads and writes the
// session's other mounts exactly as it does on the deployment's network.
[Trait("Category", "E2E")]
public class SandboxOverTunnelE2ETests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";

    // Inside the container, where nothing else listens.
    private const int BridgeBackCall = 7801;

    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "tunnel-tests", Guid.NewGuid().ToString("N"));
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"mcp-sandbox-home-{Guid.NewGuid():N}");
    private AccountsServer? _accounts;
    private RelayHost? _relay;
    private SigningKeyStore _keys = null!;
    private IContainer? _sandbox;
    private string _household = "";

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsLinux() || SandboxE2EFixture.Uid == 0 || !await DockerAsync())
        {
            return;
        }

        var root = TestHelpers.FindSolutionRoot();
        using var building = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        await TestHelpers.EnsureBaseSdkImageAsync(root, building.Token);
        await TestHelpers.EnsureImageAsync(root, E2EImages.McpSandbox, building.Token);

        _accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        _household = (await _accounts.MintOperatorAsync("Fran")).TenantId;
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "sandbox");
        _keys = SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _keyDirectory });
        _relay = await RelayHost.StartAsync(
            _accounts, AgentCredential, redis.ConnectionString, _keys, new RecordingMetricsPublisher(), on: IPAddress.Any);

        Directory.CreateDirectory(_workspace);
        _sandbox = TestContainers.Container(E2EImages.McpSandbox.ImageName, "mcp-sandbox-tunnelled")
            .WithBindMount(_workspace, SandboxE2EFixture.ContainerWorkspace, AccessMode.ReadWrite)
            .AsCompose()
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithEnvironment("MCP__TUNNEL__HUB", _relay.HubFromContainers)
            .WithEnvironment("MCP__TUNNEL__CREDENTIAL", credential.Credential)
            .WithEnvironment("MCP__TUNNEL__SERVERNAME", "sandbox")
            .WithEnvironment("MCP__TUNNEL__BACKCALLS__vfs-bridge", BridgeBackCall.ToString())
            .WithEnvironment("VFSBRIDGEURL", $"http://127.0.0.1:{BridgeBackCall}{BackCallTargetsRoute}")
            .Build();
        using var starting = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await _sandbox.StartAsync(starting.Token);
    }

    public async Task DisposeAsync()
    {
        if (_sandbox is not null)
        {
            await _sandbox.DisposeAsync();
        }

        if (_relay is not null)
        {
            await _relay.DisposeAsync();
        }

        if (_accounts is not null)
        {
            await _accounts.DisposeAsync();
        }

        foreach (var directory in new[] { _keyDirectory, _workspace }.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not a failing test.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [SkippableFact]
    public async Task ACommand_ReadsAndWritesAnotherMountThroughTheBridgeOverItsTunnel()
    {
        Skip.If(_relay is null, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await Eventually.Until(() => _relay.Registry.Find(_household, "sandbox") is not null, "the sandbox's tunnel opens");
        var notes = new MemoryDisk("notes", new Dictionary<string, string> { ["todo.md"] = "milk\n" });
        var call = _relay.Bridge.Mint(BridgeFixtures.Registry((notes, "/notes", null)), BridgeFixtures.Everything, null, null);
        await using var agent = await McpClientManager.CreateAsync(
            "jonas", "fran", "test", [_relay.OwnServers.Resolve(_household, "sandbox")!], new McpClientHandlers(),
            callTokens: new CallTokens(_keys, TimeProvider.System), ct: cts.Token);

        var result = await agent.Clients.Single().CallToolAsync(new CallToolRequestParams
        {
            Name = "fs_exec",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(""),
                ["command"] = JsonSerializer.SerializeToElement("cat /notes/todo.md && echo eggs > /notes/done.md")
            },
            Meta = new JsonObject { [VfsBridgeGrant.MetaKey] = new VfsBridgeGrant(call.Token).ToMeta() }
        }, cancellationToken: cts.Token);
        var answer = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

        JsonDocument.Parse(answer).RootElement.GetProperty("stdout").GetString().ShouldBe("milk\n", answer);
        notes.Files["done.md"].ShouldBe("eggs\n", answer);
    }

    private const string BackCallTargetsRoute = Infrastructure.Tunnels.BackCallTargets.BridgeRoute;

    private static async Task<bool> DockerAsync()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "docker", "version --format {{.Server.Os}}")
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