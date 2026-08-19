using System.Diagnostics;
using ModelContextProtocol.Client;
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
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(sandbox.Endpoint)
            }));
        var tools = await client.ListToolsAsync();

        tools.Select(t => t.Name).ShouldContain("fs_exec");
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