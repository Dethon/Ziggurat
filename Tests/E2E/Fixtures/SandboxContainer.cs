using System.Runtime.InteropServices;
using DotNet.Testcontainers.Builders;

namespace Tests.E2E.Fixtures;

// Compose's pairing for the sandbox image, in one place for every stack that starts it: the E2E
// fixture and the eval's testcontainer. The image's entrypoint is a root launcher that starts the
// server as its own uid and runs commands as PUID:PGID — here the user running the tests, so the
// bind-mounted workspace is the command's to write, as the named volume is in production. The
// launcher needs SYS_ADMIN and /dev/fuse for each command's mount namespace and the FUSE mount of
// the session's other mounts; commands never hold either. On an AppArmor host those mounts also need
// compose's profile (`DockerCompose/apparmor/ziggurat-sandbox`) installed; a host without it ignores it.
// Compose's syscall filter goes in as its content, which is what the Docker CLI sends for a path.
public static class SandboxContainer
{
    private static readonly Lazy<string> _seccomp = new(() => File.ReadAllText(
        Path.Combine(TestHelpers.FindSolutionRoot(), "DockerCompose", "seccomp", "ziggurat-sandbox.json")));

    public static ContainerBuilder AsCompose(this ContainerBuilder builder) =>
        builder
            .WithEnvironment("PUID", geteuid().ToString())
            .WithEnvironment("PGID", getegid().ToString())
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig.CapAdd = [.. parameters.HostConfig.CapAdd ?? [], "SYS_ADMIN"];
                parameters.HostConfig.SecurityOpt =
                    [.. parameters.HostConfig.SecurityOpt ?? [], "apparmor=ziggurat-sandbox", "seccomp=" + _seccomp.Value];
                parameters.HostConfig.Devices =
                [
                    .. parameters.HostConfig.Devices ?? [],
                    new Docker.DotNet.Models.DeviceMapping
                    {
                        PathOnHost = "/dev/fuse", PathInContainer = "/dev/fuse", CgroupPermissions = "rwm"
                    }
                ];
            });

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern uint getegid();
}