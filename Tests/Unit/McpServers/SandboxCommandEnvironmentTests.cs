using Domain.Contracts;
using Infrastructure.Clients.Bash;
using McpServerSandbox.Modules;
using McpServerSandbox.Settings;
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

    // The container's launcher announces its socket to the server it starts; a host with no
    // launcher — the integration fixtures — runs commands in-process, with the same environment.
    [Fact]
    public void ASandboxWithALauncherSocket_RunsCommandsThroughTheLauncher()
    {
        var services = new ServiceCollection();
        services.ConfigureMcp(Settings() with { LauncherSocket = "/run/sandbox/launcher.sock" });
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICommandRunner>().ShouldBeOfType<LauncherRunner>();
    }

    [Fact]
    public void ASandboxWithNoLauncherSocket_RunsCommandsInProcess()
    {
        var services = new ServiceCollection();
        services.ConfigureMcp(Settings());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICommandRunner>().ShouldBeOfType<BashRunner>();
    }

    private static McpSettings Settings() => new()
    {
        ContainerRoot = "/srv/jail",
        HomeDir = "/srv/jail/home/sandbox_user",
        DefaultTimeoutSeconds = 30,
        MaxTimeoutSeconds = 300,
        OutputCapBytes = 65536
    };
}