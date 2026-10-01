using Infrastructure.Clients.Bash;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Unit.McpServers;

// The sandbox container is started with the deployment's whole secrets file, so the server's own
// environment is not something a command may inherit. The shipped module hands its runner the
// minimal environment, with the home the mount publishes as its workspace.
public class SandboxCommandEnvironmentTests
{
    [Fact]
    public void TheShippedSandbox_GivesCommandsTheMinimalEnvironment()
    {
        var services = new ServiceCollection();
        McpServerRegistrations.Get("sandbox").Configure(services);
        using var provider = services.BuildServiceProvider();

        var environment = provider.GetRequiredService<BashRunnerOptions>().Environment;

        environment.ShouldNotBeNull();
        environment.Keys.ShouldBeSubsetOf(CommandEnvironment.Names);
        environment["HOME"].ShouldBe("/srv/jail/home/sandbox_user");
    }
}