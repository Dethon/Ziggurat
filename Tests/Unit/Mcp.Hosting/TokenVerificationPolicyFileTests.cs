using Domain.Security;
using Domain.Security.Signing;
using Mcp.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// How a verifier treats time is one answer, not one per server: a server forgiving less drift than
// another would refuse the agent's tokens on one container and take them on the next. So it is a
// shared policy file beside its type in Domain, the retention file's shape, read into the gate
// settings every server already binds.
public class TokenVerificationPolicyFileTests
{
    private sealed record HostSettings
    {
        public McpGateSettings Mcp { get; init; } = new();
    }

    [Fact]
    public void ThePolicyFile_ShipsBesideEveryAssemblyThatReferencesDomain() =>
        File.Exists(TokenVerificationSettings.FileName).ShouldBeTrue(
            $"{TokenVerificationSettings.FileName} should be copied to the output directory by Domain");

    [Fact]
    public void AnMcpServer_ReadsItsGatesTimingFromTheSharedFile()
    {
        var configBuilder = new ConfigurationBuilder();
        var settings = configBuilder.BindSettings<HostSettings>();

        configBuilder.Build()["mcp:tokenVerification:clockSkew"].ShouldBe("00:00:30");
        settings.Mcp.TokenVerification.ClockSkew.ShouldBe(TimeSpan.FromSeconds(30));
        settings.Mcp.TokenVerification.KeysLifetime.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void TheSharedFile_IsReadBeforeTheEnvironment()
    {
        var configBuilder = new ConfigurationBuilder();
        configBuilder.BindSettings<HostSettings>();
        var sources = configBuilder.Sources.ToList();

        var file = sources.FindIndex(source =>
            source is JsonConfigurationSource json && json.Path == TokenVerificationSettings.FileName);
        var environment = sources.FindIndex(source => source is EnvironmentVariablesConfigurationSource);

        file.ShouldBeGreaterThanOrEqualTo(0, $"{TokenVerificationSettings.FileName} should be one of the sources");
        file.ShouldBeLessThan(environment);
    }
}