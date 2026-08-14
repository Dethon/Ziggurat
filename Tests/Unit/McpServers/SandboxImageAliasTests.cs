using Domain.Tools.Files;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Integration.McpServers;

namespace Tests.Unit.McpServers;

// The mount point is a name the registry knows and the container has never heard of, unless the
// image says otherwise. The Dockerfile links it to the container root, so a path spelled the way
// the filesystem prompt teaches resolves whether it appears as a tool argument or inside a command.
//
// Both halves of that are strings in different files, and only one of them is compiled. Renaming
// the filesystem would leave commands failing in production with nothing here to notice, so the
// two are pinned to each other: the name comes off the shipped server, the alias off the shipped
// Dockerfile.
public class SandboxImageAliasTests
{
    [Fact]
    public void TheSandboxImage_AliasesTheMountPointToTheContainerRoot()
    {
        var services = new ServiceCollection();
        McpServerRegistrations.Get("sandbox").Configure(services);
        using var provider = services.BuildServiceProvider();

        var mountPoint = provider.GetRequiredService<SandboxFileSystem>().MountPoint;
        var dockerfile = File.ReadAllText(
            Path.Combine(TestHelpers.FindSolutionRoot(), "McpServerSandbox", "Dockerfile"));

        dockerfile.Contains($"ln -s / {mountPoint}", StringComparison.Ordinal).ShouldBeTrue(
            $"the sandbox image must alias {mountPoint} to the container root, or a command "
            + "written in the spelling the filesystem prompt teaches fails against bash");
    }
}