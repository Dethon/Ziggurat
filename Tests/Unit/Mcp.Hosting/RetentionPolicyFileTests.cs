using Agent.Modules;
using Domain.DTOs;
using Mcp.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// The retention policy is one file, not a block per host. Three processes write topic keys — the
// agent, the chat channel and the voice channel — and a conversation that aged on a different
// clock depending on which of them last wrote to it would be the defect this shape exists to
// prevent.
//
// What that costs is invisible from any one host: each reads its own configuration and would look
// perfectly correct while disagreeing with the other two. So these assert the shared file itself.
public class RetentionPolicyFileTests
{
    private sealed record HostSettings
    {
        public RetentionSettings Retention { get; init; } = new();
    }

    // Shipped from Domain, so it reaches the output of everything that references Domain — which
    // is every host that writes a topic, and this test project. A Content item that stopped
    // flowing would leave every host silently on the type's defaults.
    [Fact]
    public void ThePolicyFile_ShipsBesideEveryAssemblyThatReferencesDomain()
    {
        File.Exists(RetentionSettings.FileName).ShouldBeTrue(
            $"{RetentionSettings.FileName} should be copied to the output directory by Domain");
    }

    [Fact]
    public void AnMcpServer_ReadsItsHorizonsFromTheSharedFile()
    {
        var settings = new ConfigurationBuilder().BindSettings<HostSettings>();

        settings.Retention.ArchiveHorizon.ShouldBe(TimeSpan.FromDays(60));
        settings.Retention.PurgeHorizon.ShouldBe(TimeSpan.FromDays(365));
        settings.Retention.PageSize.ShouldBe(20);
    }

    // The agent host binds its settings through its own entry point rather than through
    // BindSettings, so the two reading the same file is the thing worth pinning.
    [Fact]
    public void TheAgentHost_ReadsTheSameFileAsEveryMcpServer()
    {
        var fromTheAgent = new ConfigurationBuilder().GetSettings().Retention;
        var fromAServer = new ConfigurationBuilder().BindSettings<HostSettings>().Retention;

        fromTheAgent.ShouldBe(fromAServer);
    }

    // One container is still overridable for a test run — the E2E stack serves one row a page that
    // way — so the shared file has to lose to the environment rather than win over it.
    //
    // Asked of the source order rather than of an override actually taking effect: the environment
    // an override would have to be written to is the process's own, and a suite running at full
    // width would hand that override to whatever else bound configuration in the same window. The
    // order is the whole mechanism — a later source wins, which is the framework's own rule — so
    // the file being added ahead of the environment is what there is to get wrong here.
    [Fact]
    public void TheSharedFile_IsReadBeforeTheEnvironment()
    {
        var configBuilder = new ConfigurationBuilder();
        configBuilder.BindSettings<HostSettings>();
        var sources = configBuilder.Sources.ToList();

        var file = sources.FindIndex(source =>
            source is JsonConfigurationSource json && json.Path == RetentionSettings.FileName);
        var environment = sources.FindIndex(source => source is EnvironmentVariablesConfigurationSource);

        file.ShouldBeGreaterThanOrEqualTo(0, $"{RetentionSettings.FileName} should be one of the sources");
        environment.ShouldBeGreaterThanOrEqualTo(0, "the environment should be one of the sources");
        file.ShouldBeLessThan(environment);
    }
}