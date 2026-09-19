using Domain.Judgments;
using Domain.Tools.Web;
using Infrastructure.Agents.ChatClients;
using McpServerWebSearch.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Unit.McpServerWebSearch;

// The judge on the browse server, through its real registration: an empty key is a judge that
// answers absence with no call made, and the settings that tune the judgment ship in the server's
// own appsettings with the defaults the spec names.
public class ModalJudgmentRegistrationTests
{
    [Fact]
    public async Task AnEmptyKey_RegistersAJudgeThatAnswersUnconfigured_AndNoKeepAlive()
    {
        await using var provider = Build(McpServerRegistrations.Get("websearch"));

        var judge = provider.GetRequiredService<IJudge>();
        var outcome = await judge.JudgeAsync(
            new JudgmentRequest(new System.Text.Json.Nodes.JsonObject(), new Dictionary<string, JudgmentQuestion>(), JudgmentRequest.NoTurn),
            CancellationToken.None);

        outcome.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Unconfigured);
        provider.GetServices<IHostedService>().OfType<HostedConnectionKeepAlive>().ShouldBeEmpty();
    }

    [Fact]
    public void TheShippedAppSettings_CarryTheJudgeAndTheJudgment()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(McpServerRegistrations.RepoRoot, "McpServerWebSearch/appsettings.json"))
            .Build();

        var settings = configuration.Get<McpSettings>()!;

        settings.TypeSafe.ApiKey.ShouldBeEmpty();
        settings.TypeSafe.Model.ShouldBe("jev-1.13.0");
        settings.Judgment.ShouldBe(new ModalJudgmentSettings { DeadlineMs = 1000, Confidence = 0.6, MaxControls = 20 });
    }

    private static ServiceProvider Build(McpServerRow row)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The MCP hosted services want a lifetime only a real host builder supplies; enumerating
        // IHostedService constructs them all, and nothing here starts them.
        services.AddSingleton(Mock.Of<IHostApplicationLifetime>());
        row.Configure(services);
        return services.BuildServiceProvider();
    }
}