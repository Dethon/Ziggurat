using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Security;
using Domain.Security.Signing;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Channels;

// The error rule has one home, and a server that asks for it twice gets it once. That is the whole
// reason it moved out of AddChannelServer: a dual-role server asks as a tool server and again as a
// channel server, and two filters nested around each other would let the outer one convert the very
// cancellation the inner deliberately rethrows.
//
// The result a failure comes back as is pinned over the wire by ChannelServerExtensionsTests. What a
// failure is reported as is pinned here, on a real host: a shared server answers every household, so
// its failure is counted under the household the verified call acted for, and under the deployment's
// own only when the call acted for nobody.
public class CallToolErrorFilterTests
{
    private const string Household = "a11ce0000000000000000000000000a1";

    [Fact]
    public void AskingForTheFilterTwice_InstallsItOnce() =>
        CallToolFilterCount(builder => builder
            .AddCallToolErrorFilter(null)
            .AddCallToolErrorFilter(null))
            .ShouldBe(1);

    [Fact]
    public void AChannelServerThatAlsoAsksForTheFilter_GetsOne() =>
        CallToolFilterCount(builder => builder
            .AddCallToolErrorFilter(Marked)
            .AddChannelServer(DeliveryPolicy.Broadcast))
            .ShouldBe(1);

    [Fact]
    public async Task AToolThatFailsForAPerson_IsReportedUnderTheirHousehold()
    {
        var metrics = new RecordingMetrics();
        await using var server = await StartAsync(metrics);
        await using var client = await McpClient.CreateAsync(McpTestSecret.Transport(server.Endpoint));

        var result = await CallFailsAsync(client, IdentityClaims.Person("fran", Household, ChannelIds.SignalR, []));

        result.IsError.ShouldBe(true);
        await Eventually.Until(() => metrics.Errors.Count > 0, "the failure is reported");
        var error = metrics.Errors.ShouldHaveSingleItem();
        error.TenantId.ShouldBe(Household);
        error.Service.ShouldBe(McpTestSecret.Audience);
        error.Message.ShouldContain("fails");
        error.Message.ShouldContain("the far side fell over");
    }

    [Fact]
    public async Task AToolThatFailsForNobody_IsReportedAsTheDeploymentsOwn()
    {
        var metrics = new RecordingMetrics();
        await using var server = await StartAsync(metrics);
        await using var client = await McpClient.CreateAsync(McpTestSecret.Transport(server.Endpoint));

        await CallFailsAsync(client, IdentityClaims.Nobody(ChannelIds.SignalR));

        await Eventually.Until(() => metrics.Errors.Count > 0, "the failure is reported");
        metrics.Errors.ShouldHaveSingleItem().TenantId.ShouldBeNull();
    }

    private sealed record GatedSettings(McpGateSettings Mcp) : IMcpHostSettings;

    private static Task<RunningServer> StartAsync(RecordingMetrics metrics) =>
        InMemoryMcpServer.StartAsync(services => services
            .AddSingleton<IMetricsPublisher>(metrics)
            .AddToolServer(new GatedSettings(McpTestSecret.Gate))
            .WithTools<FailingTools>());

    private static ValueTask<CallToolResult> CallFailsAsync(McpClient client, IdentityClaims identity)
    {
        var turn = new ConversationContext("jonas", "conv-1", "fran", new ReplyTarget(ChannelIds.SignalR, "conv-1"))
        {
            Identity = identity
        };
        return client.CallToolAsync(new CallToolRequestParams
        {
            Name = "fails",
            Meta = new JsonObject
            {
                [ChannelProtocol.ConversationContextMetaKey] =
                    JsonSerializer.SerializeToNode(turn, ChannelProtocol.SerializerOptions)
            }
        });
    }

    private static CallToolResult Marked(Exception ex) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = ex.Message }]
    };

    private static int CallToolFilterCount(Action<IMcpServerBuilder> configure) =>
        McpServerProbe.CallToolFilterCount(services => configure(services.AddMcpServer()));

    [McpServerToolType]
    public sealed class FailingTools
    {
        [McpServerTool(Name = "fails")]
        [Description("Always fails.")]
        public static string Fails() => throw new InvalidOperationException("the far side fell over");
    }

    private sealed class RecordingMetrics : IMetricsPublisher
    {
        private readonly Lock _lock = new();
        private readonly List<ErrorEvent> _errors = [];

        public IReadOnlyList<ErrorEvent> Errors
        {
            get
            {
                lock (_lock)
                {
                    return [.. _errors];
                }
            }
        }

        public void Publish(MetricEvent metricEvent)
        {
            if (metricEvent is ErrorEvent error)
            {
                lock (_lock)
                {
                    _errors.Add(error);
                }
            }
        }
    }
}