using Domain.DTOs;
using Domain.DTOs.Metrics;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;

namespace Tests.Integration.Memory;

// Memory's models — extraction and dreaming — are asked through one client per payer: what reaches
// OpenRouter carries the household's own key, or the deployment's for the home and for nobody, and
// what it cost is counted under the household it was for.
public sealed class PaidChatClientsTests : IAsyncLifetime
{
    private const string Household = "0123456789abcdef0123456789abcdef";

    private FakeOpenRouter _model = null!;
    private readonly RecordingMetricsPublisher _metrics = new();

    public async Task InitializeAsync() => _model = await FakeOpenRouter.StartAsync();

    public async Task DisposeAsync() => await _model.DisposeAsync();

    [Fact]
    public async Task AHouseholdsCall_IsSentWithItsKeyAndCountedUnderIt()
    {
        var clients = new PaidChatClients(_model.ApiUrl, "deployment-key", "m", _metrics);

        await clients.For(new ModelPayer(Household, new ModelKey("household-key")))
            .GetResponseAsync([new ChatMessage(ChatRole.User, "hola")]);

        _model.Requests.ShouldHaveSingleItem().Authorization.ShouldBe("Bearer household-key");
        _metrics.Published.OfType<TokenUsageEvent>().ShouldHaveSingleItem().TenantId.ShouldBe(Household);
    }

    [Fact]
    public async Task ACallNamingNoKey_IsSentWithTheDeploymentsKey()
    {
        var clients = new PaidChatClients(_model.ApiUrl, "deployment-key", "m", _metrics);

        await clients.For(new ModelPayer(Household, null)).GetResponseAsync([new ChatMessage(ChatRole.User, "hola")]);
        await clients.For(ModelPayer.Deployment).GetResponseAsync([new ChatMessage(ChatRole.User, "hola")]);

        _model.Requests.ShouldAllBe(request => request.Authorization == "Bearer deployment-key");
        _metrics.Published.OfType<TokenUsageEvent>().Select(e => e.TenantId).ShouldBe([Household, null]);
    }
}