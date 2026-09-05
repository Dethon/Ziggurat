using Domain.DTOs;
using Mcp.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// Where an unprompted fire lands when its author named no channel is one answer, not one per
// server: a schedule and a watch that defaulted to different channels would send the same
// person's warnings to two places depending on which server raised them. So the default is a
// shared policy file beside its type in Domain, the retention file's shape.
public class DeliveryPolicyFileTests
{
    private sealed record HostSettings
    {
        public DeliverySettings Delivery { get; init; } = new();
    }

    [Fact]
    public void ThePolicyFile_ShipsBesideEveryAssemblyThatReferencesDomain()
    {
        File.Exists(DeliverySettings.FileName).ShouldBeTrue(
            $"{DeliverySettings.FileName} should be copied to the output directory by Domain");
    }

    [Fact]
    public void AnMcpServer_ReadsTheDefaultDeliveryFromTheSharedFile()
    {
        var settings = new ConfigurationBuilder().BindSettings<HostSettings>();

        settings.Delivery.DefaultDeliverTo.ShouldBe(["signalr"]);
    }

    [Fact]
    public void TheSharedFile_IsReadBeforeTheEnvironment()
    {
        var configBuilder = new ConfigurationBuilder();
        configBuilder.BindSettings<HostSettings>();
        var sources = configBuilder.Sources.ToList();

        var file = sources.FindIndex(source =>
            source is JsonConfigurationSource json && json.Path == DeliverySettings.FileName);
        var environment = sources.FindIndex(source => source is EnvironmentVariablesConfigurationSource);

        file.ShouldBeGreaterThanOrEqualTo(0, $"{DeliverySettings.FileName} should be one of the sources");
        file.ShouldBeLessThan(environment);
    }
}