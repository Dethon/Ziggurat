using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Eval.Fixtures;

// The sandbox container, proven before a model pays for a run against it: the image starts, the
// MCP endpoint answers, and the tool the whole exec family exists for is actually advertised.
// Skipped where the pairing cannot be produced — the same guard SandboxE2EFixture applies.
public class EvalSandboxTests
{
    [SkippableFact]
    public async Task TheSandbox_StartsAndAdvertisesExec()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The sandbox pairing needs Linux.");
        Skip.IfNot(await DockerIsRunningAsync(), "Docker is not running.");

        await using var sandbox = await EvalSandbox.StartAsync();

        await using var client = await McpClient.CreateAsync(
            McpTestSecret.Transport(sandbox.Endpoint));
        var tools = await client.ListToolsAsync();

        tools.Select(t => t.Name).ShouldContain("fs_exec");
    }

    // The eval's own wiring of the shell over the mounts: the container reaches a bridge the stack
    // hosts, and a command run with a minted call's token sees that call's mounts. A family run
    // whose commands could not see /vault would read as the model ignoring the route.
    [SkippableFact]
    public async Task TheSandbox_ServesACallsMountsThroughABridgeTheStackHosts()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The sandbox pairing needs Linux.");
        Skip.IfNot(await DockerIsRunningAsync(), "Docker is not running.");
        var bridge = new VfsBridge(TimeProvider.System, new VfsBridgeSettings());
        var (url, host, port) = await EvalBridgeHost.StartAsync(bridge);
        try
        {
            await using var sandbox = await EvalSandbox.StartAsync(url);
            await using var client = await McpClient.CreateAsync(McpTestSecret.Transport(sandbox.Endpoint));
            var call = bridge.Mint(Unit.Domain.Tools.FileSystem.Bridge.BridgeFixtures.Registry(
                (new Unit.Domain.Tools.FileSystem.Bridge.MemoryDisk(
                    "vault", new Dictionary<string, string> { ["hello.md"] = "served\n" }), "/vault", null)), Unit.Domain.Tools.FileSystem.Bridge.BridgeFixtures.Everything, null, null);

            var result = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "fs_exec",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(""),
                    ["command"] = JsonSerializer.SerializeToElement("cat /vault/hello.md")
                },
                Meta = new JsonObject { [VfsBridgeGrant.MetaKey] = new VfsBridgeGrant(call.Token).ToMeta() }
            });

            string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text)).ShouldContain("served");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            Integration.Fixtures.TestPort.Release(port);
        }
    }

    private static async Task<bool> DockerIsRunningAsync()
    {
        try
        {
            using var docker = Process.Start(new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (docker is null)
            {
                return false;
            }

            await docker.WaitForExitAsync();
            return docker.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}