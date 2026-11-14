using System.Collections.Concurrent;
using System.ComponentModel;
using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security;
using Infrastructure.Clients.Channels;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Channels;

// An approval is asked of the channel by the agent host, not by the model, so it is a hop that
// used to carry no `_meta`. The channel that reads an answer has to know what the turn asked for —
// whose question it is, and whether it was addressed to the local box, which is never put to a
// hosted judge — and the turn rides with the question as a field of it, never as whatever call
// happens to be in flight.
public class McpChannelConnectionApprovalMetaTests
{
    private sealed record ProbeSettings(string Name) : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    private static readonly Account _fran = new("home", "fran");

    private static readonly IReadOnlyList<ToolApprovalRequest> _call =
        [new ToolApprovalRequest(null, "mcp__home__call", new Dictionary<string, object?>())];

    private static async Task<(RunningServer Server, McpChannelConnection Connection)> ConnectAsync()
    {
        var server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ProbeSettings("probe"))
            .WithTools<ProbeApprovalTools>());
        var connection = new McpChannelConnection("test", mcpSecret: McpTestSecret.Value, callTokens: McpTestSecret.CallTokens);
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);
        return (server, connection);
    }

    [Fact]
    public async Task AnApprovalAskedForATurn_CarriesTheTurnsContextAndTheQuestionsIdToTheChannel()
    {
        var (server, connection) = await ConnectAsync();
        await using var _ = server;
        await using var __ = connection;
        var turn = new ConversationContext(
            "jack", "conv-1", "fran", new ReplyTarget("signalr", "conv-1"), "lemonade/qwen3");
        var ask = ApprovalAsk.For("conv-1", _call, turn);

        var result = await connection.AskApprovalAsync(ask, CancellationToken.None);

        result.ShouldBe(ToolApprovalResult.Approved);
        ProbeApprovalTools.Asked.ShouldContain(ask.ApprovalId);
    }

    [Fact]
    public async Task AQuestionTheChannelCouldNotShow_IsNoAnswer()
    {
        var (server, connection) = await ConnectAsync();
        await using var _ = server;
        await using var __ = connection;

        var result = await connection.AskApprovalAsync(
            ApprovalAsk.For("conv-unshown", _call, null), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task AnOffer_NamesItsAskerToTheChannel_AndASettleNamesTheAnswer()
    {
        var (server, connection) = await ConnectAsync();
        await using var _ = server;
        await using var __ = connection;
        var turn = new ConversationContext(
            "jack", "conv-1", "fran", new ReplyTarget("voice", "conv-1"),
            Principal: new Principal("voice", "fran", _fran));
        var ask = ApprovalAsk.For("conv-1", _call, turn);

        var offered = await connection.OfferApprovalAsync(ask, CancellationToken.None);
        await connection.SettleApprovalAsync(ask.ApprovalId, ToolApprovalResult.Approved, CancellationToken.None);

        offered.ShouldBe(ToolApprovalResult.ApprovedAndRemember);
        ProbeApprovalTools.Offers.ShouldContain((ask.ApprovalId, _fran, "jack"));
        ProbeApprovalTools.Settles.ShouldContain((ask.ApprovalId, ToolApprovalResult.Approved));
    }

    // Calling the question off is how a prompt still waiting — a satellite still asking aloud —
    // learns it was answered elsewhere, so the cancellation has to reach the tool itself.
    [Fact]
    public async Task CallingAQuestionOff_ReachesTheToolStillAskingIt()
    {
        var (server, connection) = await ConnectAsync();
        await using var _ = server;
        await using var __ = connection;
        using var asking = new CancellationTokenSource();
        var ask = ApprovalAsk.For("conv-waits", _call, null);

        var answered = connection.AskApprovalAsync(ask, asking.Token);
        await Eventually.Until(() => ProbeApprovalTools.Asked.Contains(ask.ApprovalId), "the channel to be asked");
        await asking.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(answered);
        await Eventually.Until(() => ProbeApprovalTools.CalledOff.Contains(ask.ApprovalId), "the tool to see the cancellation");
    }

    [McpServerToolType]
    public sealed class ProbeApprovalTools
    {
        public static readonly ConcurrentBag<string> Asked = [];
        public static readonly ConcurrentBag<string> CalledOff = [];
        public static readonly ConcurrentBag<(string, Account, string?)> Offers = [];
        public static readonly ConcurrentBag<(string, ToolApprovalResult)> Settles = [];

        // Approves only when the call's `_meta` names a turn that asked for the local box, so the
        // test reads what the server saw through the result the connection always returns.
        [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
        [Description("Approves when the caller's turn asked for a Lemonade model.")]
        public static async Task<string> RequestApproval(
            string conversationId, ApprovalMode mode, IReadOnlyList<ToolApprovalRequest> requests,
            RequestContext<CallToolRequestParams> context, string? approvalId = null,
            CancellationToken cancellationToken = default)
        {
            Asked.Add(approvalId ?? "");
            switch (conversationId)
            {
                case "conv-unshown":
                    return ChannelProtocol.NoAnswer;
                case "conv-waits":
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        CalledOff.Add(approvalId ?? "");
                        throw;
                    }

                    return "approved";
                default:
                    return ConversationScope.Parse(context.Params?.Meta)?.ConfigPatchModel == "lemonade/qwen3"
                        ? "approved"
                        : "rejected";
            }
        }

        [McpServerTool(Name = ChannelProtocol.OfferApprovalTool)]
        [Description("Records the offer and answers with the keyboard's spelling of approve-and-remember.")]
        public static string OfferApproval(
            string approvalId, string conversationId, Account asker, IReadOnlyList<ToolApprovalRequest> requests,
            string? agentId = null)
        {
            Offers.Add((approvalId, asker, agentId));
            return "approved_and_remember";
        }

        [McpServerTool(Name = ChannelProtocol.SettleApprovalTool)]
        [Description("Records the settle.")]
        public static string SettleApproval(string approvalId, ToolApprovalResult result)
        {
            Settles.Add((approvalId, result));
            return "settled";
        }
    }
}