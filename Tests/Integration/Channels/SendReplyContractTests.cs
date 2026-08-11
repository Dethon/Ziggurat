using System.Text.Json;
using Domain.DTOs.Channel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Channels;

// Every reply the agent sends says which turn it answers and whether that turn was the agent's own
// idea. Both travel on send_reply, so every channel server has to accept them — a channel that
// silently drops the key would fail in production as satellites that stopped answering, not here.
//
// Driven off the channel-capable rows of the one server table, so a new channel is one new row and
// this pin comes with it. The two no-outbound-surface servers reach the same shared stub through
// their own rows: it is one registration serving both, and it is the one send_reply in the repo
// that nobody writes per channel.
public class SendReplyContractTests
{
    private const string TurnKeyParameter = "turnKey";
    private const string AgentInitiatedParameter = "agentInitiated";

    public static TheoryData<string, Action<IServiceCollection>> Servers =>
        McpServerRegistrations.ChannelServers.Aggregate(
            new TheoryData<string, Action<IServiceCollection>>(),
            (data, row) =>
            {
                data.Add(row.Id, row.Configure);
                return data;
            });

    [Theory]
    [MemberData(nameof(Servers))]
    public void ChannelServerRegistration_DeclaresTheTurnKeyOnSendReply(
        string channelId, Action<IServiceCollection> configureChannel)
    {
        var parameters = SendReplyParametersOf(configureChannel);

        parameters.ShouldContain(
            TurnKeyParameter, $"{channelId} must accept the turn key its replies are answering under");
        parameters.ShouldContain(
            AgentInitiatedParameter, $"{channelId} must accept whether the turn was agent-initiated");
    }

    // Nullable, because a channel is free to ignore both — and because a required parameter would
    // make every one of them a breaking change for a channel server deployed a version behind.
    [Theory]
    [MemberData(nameof(Servers))]
    public void ChannelServerRegistration_LeavesTheTwoNewParametersOptional(
        string channelId, Action<IServiceCollection> configureChannel)
    {
        var schema = SendReplySchemaOf(configureChannel);

        var required = schema.TryGetProperty("required", out var value)
            ? value.EnumerateArray().Select(item => item.GetString()).ToList()
            : [];

        required.ShouldNotContain(TurnKeyParameter, $"{channelId} must not require the turn key");
        required.ShouldNotContain(
            AgentInitiatedParameter, $"{channelId} must not require the agent-initiated flag");
    }

    private static IReadOnlyList<string> SendReplyParametersOf(Action<IServiceCollection> configure) =>
        SendReplySchemaOf(configure)
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();

    private static JsonElement SendReplySchemaOf(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);

        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>()
            .Single(tool => tool.ProtocolTool.Name == ChannelProtocol.SendReplyTool)
            .ProtocolTool.InputSchema;
    }
}