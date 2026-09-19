using Domain.Contracts;
using Domain.Judgments;
using Domain.Tools.Web;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Judgments;
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

    // This server makes no other hosted call, so nothing else holds the connection Jev is asked
    // on: the keep-alive is its own, pinging OpenRouter's key metadata beside the decisions path.
    [Fact]
    public void AnOpenRouterKey_KeepsTheConnectionJevIsAskedOnAlive()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(Mock.Of<IMetricsPublisher>());
        services.AddTypeSafeJudge(new TypeSafeOptions().KeyedBy("or-key"));
        using var provider = services.BuildServiceProvider();

        var keepAlive = provider.GetServices<IHostedService>().OfType<HostedConnectionKeepAlive>().ShouldHaveSingleItem();

        keepAlive.Options.BaseAddress.ShouldBe("https://openrouter.ai/api/");
        keepAlive.Options.NonBillableEndpoint.ShouldBe("v1/key");
        keepAlive.Options.ApiKey.ShouldBe("or-key");
    }

    [Fact]
    public void TheShippedAppSettings_CarryTheJudgeAndTheJudgment()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(McpServerRegistrations.RepoRoot, "McpServerWebSearch/appsettings.json"))
            .Build();

        var settings = configuration.Get<McpSettings>()!;

        settings.OpenRouter.ApiKey.ShouldBeEmpty();
        settings.TypeSafe.ApiUrl.ShouldBe("https://openrouter.ai/api/");
        settings.TypeSafe.Model.ShouldBe("typesafe/jev-1.13-20260917");
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