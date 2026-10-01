using System.Runtime.InteropServices;
using DotNet.Testcontainers.Builders;

namespace Tests.E2E.Fixtures;

// Compose's pairing for the sandbox image, in one place for every stack that starts it: the E2E
// fixture and the eval's testcontainer. The image's entrypoint is a root launcher that starts the
// server as its own uid and runs commands as PUID:PGID — here the user running the tests, so the
// bind-mounted workspace is the command's to write, as the named volume is in production. The
// launcher needs SYS_ADMIN for each command's mount namespace; commands never hold it.
public static class SandboxContainer
{
    public static ContainerBuilder AsCompose(this ContainerBuilder builder) =>
        builder
            .WithEnvironment("PUID", geteuid().ToString())
            .WithEnvironment("PGID", getegid().ToString())
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig.CapAdd = [.. parameters.HostConfig.CapAdd ?? [], "SYS_ADMIN"];
            });

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern uint getegid();
}