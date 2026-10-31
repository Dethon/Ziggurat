using Accounts.Settings;
using Agent.Modules;
using Domain.Security;
using Domain.Security.Signing;
using Mcp.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Observability;
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

    // Every host that verifies a token, besides the servers above: the agent (call tokens' issuer and
    // the assertions' and delegations' verifier), the dashboard (assertions), and the accounts
    // service, which verifies nothing but tells its operator when a retired key's last assertion is
    // past every verifier's skew. Each must read the shared file, ahead of the environment.
    public static TheoryData<string> VerifyingHosts => ["agent", "observability", "accounts"];

    [Theory]
    [MemberData(nameof(VerifyingHosts))]
    public void EveryVerifyingHost_ReadsTheSharedFileBeforeTheEnvironment(string host)
    {
        // As a web host hands its configuration over: a manager, the environment already among its sources.
        var configBuilder = new ConfigurationManager();
        configBuilder.AddEnvironmentVariables();
        var skew = Load(host, configBuilder);
        var sources = configBuilder.Sources.ToList();

        var file = sources.FindIndex(source =>
            source is JsonConfigurationSource json && json.Path == TokenVerificationSettings.FileName);
        var environment = sources.FindLastIndex(source => source is EnvironmentVariablesConfigurationSource);

        file.ShouldBeGreaterThanOrEqualTo(0, $"{host} should read {TokenVerificationSettings.FileName}");
        file.ShouldBeLessThan(environment, $"{host} should read the environment after the shared file");
        configBuilder["mcp:tokenVerification:clockSkew"].ShouldBe("00:00:30");
        skew.ShouldBe(TimeSpan.FromSeconds(30));
    }

    private static TimeSpan Load(string host, IConfigurationBuilder configBuilder) => host switch
    {
        "agent" => configBuilder.GetSettings().Mcp.TokenVerification.ClockSkew,
        "observability" => configBuilder.GetTokenVerification().ClockSkew,
        "accounts" => AccountsSettings.Read(configBuilder).Mcp.TokenVerification.ClockSkew,
        _ => throw new ArgumentOutOfRangeException(nameof(host), host, null)
    };
}