using System.Diagnostics;
using System.Text.Json;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;

namespace Tests.Integration.Fixtures;

// The machine's NVIDIA card, for a container that can use it: Docker hands it over only where it has
// the nvidia runtime, which is what `docker info` lists. Asked once per test process.
internal static class NvidiaGpu
{
    private static readonly Lazy<bool> _available = new(() => InRuntimes(DockerRuntimes()));

    public static bool Available => _available.Value;

    public static bool InRuntimes(string runtimesJson)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(runtimesJson)
                   && JsonDocument.Parse(runtimesJson).RootElement.TryGetProperty("nvidia", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Every card, as compose's `driver: nvidia, count: all` asks for them.
    public static ContainerBuilder WithTheCard(this ContainerBuilder builder) =>
        builder
            .WithEnvironment("NVIDIA_DRIVER_CAPABILITIES", "compute,utility")
            .WithCreateParameterModifier(p => (p.HostConfig ??= new HostConfig()).DeviceRequests =
            [
                new DeviceRequest { Driver = "nvidia", Count = -1, Capabilities = [["gpu"]] }
            ]);

    private static string DockerRuntimes()
    {
        try
        {
            using var docker = Process.Start(new ProcessStartInfo("docker", "info --format \"{{json .Runtimes}}\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            var output = docker.StandardOutput.ReadToEnd();
            docker.WaitForExit(30_000);
            return docker.ExitCode == 0 ? output.Trim() : "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}