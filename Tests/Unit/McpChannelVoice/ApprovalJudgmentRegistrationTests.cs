using Domain.Judgments;
using Infrastructure.Agents.ChatClients;
using McpChannelVoice.Modules;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// The judge on the voice channel through its real registration: the tool's reader resolves, and
// an empty key is a judge that answers absence with no call made and no keep-alive started.
public class ApprovalJudgmentRegistrationTests
{
    // Enumerating IHostedService resolves the multiplexer; abortConnect=false hands back a
    // disconnected one instead of throwing, as MetricsRegistrationContractTests relies on.
    private const string UnreachableRedis =
        "127.0.0.1:1,abortConnect=false,connectTimeout=100,connectRetry=1";

    [Fact]
    public async Task AnEmptyKey_RegistersAReaderOverAJudgeThatAnswersUnconfigured_AndNoKeepAlive()
    {
        await using var provider = Build(new VoiceSettings { RedisConnectionString = UnreachableRedis });

        provider.GetRequiredService<IApprovalReader>().ShouldBeOfType<JudgedApprovalReader>();
        var outcome = await provider.GetRequiredService<IJudge>().JudgeAsync(
            new JudgmentRequest(new System.Text.Json.Nodes.JsonObject(), new Dictionary<string, JudgmentQuestion>(), JudgmentRequest.NoTurn),
            CancellationToken.None);

        outcome.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Unconfigured);
        provider.GetServices<IHostedService>().OfType<HostedConnectionKeepAlive>().ShouldBeEmpty();
    }

    private static ServiceProvider Build(VoiceSettings settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The MCP hosted services want a lifetime only a real host builder supplies; enumerating
        // IHostedService constructs them all, and nothing here starts them.
        services.AddSingleton(Mock.Of<IHostApplicationLifetime>());
        services.ConfigureVoiceChannel(settings);
        return services.BuildServiceProvider();
    }
}