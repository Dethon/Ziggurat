using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Domain.Security;
using Domain.Skills;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    // The connections the agent holds and every one it comes to hold: a household's own channel
    // server's appears when its tunnel first opens, after the monitor started.
    IConnectionSet<IChannelConnection> channels,
    IAgentFactory agentFactory,
    ChatThreadResolver threadResolver,
    IMetricsPublisher metricsPublisher,
    IMemoryRecallHook? memoryRecallHook,
    ILogger<ChatMonitor> logger,
    // Who a person's turn acts for: the accounts service's assertion each one arrives with, checked
    // against its keys. Required, since a monitor that believed the channels would act for whoever
    // they named.
    AssertionVerifier assertions,
    // Optional because a host may configure none — a test harness, or a deployment where every
    // channel names its agent. With none configured a message that names no agent is refused
    // when the agent is built, which is the point: nothing guesses.
    AgentDefaults? agentDefaults = null,
    // Nullable exactly as the recall hook is: a host without a judge preloads nothing.
    ISkillPreloader? skillPreloader = null,
    // Who a fire acts for: the delegation the agent signed for the object raising it, checked against
    // its own keys. Optional because its absence is safe — a monitor that checks no delegation
    // believes none, and every fire acts for nobody.
    DelegationVerifier? delegations = null,
    // Where the agent mints a fire's topic when only an attach-only channel delivers it. Optional
    // for the hosts that keep no topic store; the agent's own host registers one.
    IConversationFactory? topics = null,
    // The households' agents, whose default answers a message its channel has none for. Optional as
    // the defaults are: with none, such a message is refused when the agent is built.
    IAgentDefinitionProvider? agentDefinitions = null,
    // Whether the household still lets the agent act for the person a turn's assertion or a fire's
    // delegation names: an owner may have disabled them since either was signed. Optional as the
    // verifier is, for a harness with no households; the agent's own host registers it.
    IAccountStanding? standing = null)
{
    // A person's turn whose assertion was not believed, on the errors feed beside the servers'
    // CallTokenRefused: a channel server that misbehaves, an accounts service the agent cannot
    // reach, or a turn that sat too long on its way here.
    public const string AssertionRefusedErrorType = "AssertionRefused";

    // A fire that carried a delegation the agent did not believe, and so ran as nobody: a key since
    // retired or lost (redelegate), an object changed outside the turns that wrote it, or a server
    // presenting what it was never given.
    public const string DelegationRefusedErrorType = "DelegationRefused";

    // A turn whose verified household is not the household of the channel connection it came on: a
    // household's own server is believed for that household alone, so this is a server misbehaving.
    public const string WrongHouseholdErrorType = "WrongHousehold";

    // A turn of a member an owner disabled, refused before anything was built for it.
    public const string AccountDisabledErrorType = "AccountDisabled";

    // A turn of a household that set no OpenRouter key, refused before its agent was asked anything.
    public const string HouseholdWithoutKeyErrorType = "HouseholdWithoutKey";

    // A turn or a fire of a household the operator suspended, refused as it arrived.
    public const string HouseholdSuspendedErrorType = "HouseholdSuspended";

    // A turn or a fire refused because whether its household runs, or its person is still let in,
    // could not be read just now: neither is ever taken for granted.
    public const string StandingUnreadableErrorType = "StandingUnreadable";

    private readonly DeliveryTargetResolver _targetResolver = new(channels, logger, topics);
    private readonly ReplyDispatcher _replyDispatcher = new(metricsPublisher, logger);

    public async Task Monitor(CancellationToken cancellationToken)
    {
        try
        {
            var merged = channels
                .WatchAsync(cancellationToken)
                .Select(ch => ch.Messages.Select(async ValueTask<(IChannelConnection Channel, ChannelMessage Message)> (m, ct) =>
                    (ch, await RouteToAgentAsync(m, ct))))
                .Merge(cancellationToken);

            var groups = merged
                .GroupByStreaming(
                    (x, _) => ValueTask.FromResult(new AgentKey(x.Message.ConversationId, x.Message.AgentId)),
                    // A message can race into a group just as its teardown completes it; the
                    // write is refused, and the refusal must name the turn like any other drop.
                    x => ConversationGroup.LogDroppedTurn(logger, x.Message),
                    cancellationToken)
                .Select(group => ProcessChatThread(group.Key, group, cancellationToken))
                .Merge(cancellationToken);

            await foreach (var _ in groups)
            { }
        }
        // Shutdown, not a fault. It reaches here as the merge and the groups unwind, and reporting
        // it as an error put a red line in the dashboard on every ordinary deploy.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
            metricsPublisher.Publish(new ErrorEvent
            {
                Service = "agent",
                ErrorType = ex.GetType().Name,
                Message = ex.Message
            });

            // Reported here, decided elsewhere. Swallowing it left the caller unable to tell a
            // monitor that finished from one that died, so the supervising loop restarted both the
            // same way and as fast as the failure could recur.
            throw;
        }
    }

    // Resolved here, upstream of the grouping, so one id serves the whole turn: the group key, the
    // agent built for it, the conversation context stamped on the message and the memory written
    // from it. Resolving it later would let a message that named no agent group apart from one
    // that named the same agent explicitly, running two agents over one conversation.
    //
    // The household is the one the message's channel says it acts for, believed only for this: the
    // agent is then built for the household its verified identity names, and an id that household
    // has no agent by is refused there.
    private async ValueTask<ChannelMessage> RouteToAgentAsync(ChannelMessage message, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(message.AgentId))
        {
            return message;
        }

        var routed = agentDefaults?.For(message.ChannelId) ?? await HouseholdDefaultAsync(message, ct);
        return routed is null ? message : message with { AgentId = routed };
    }

    // A household the agent cannot read just now routes nothing: the message is refused as one with
    // no default, which the person is told, rather than taking the monitor down with it.
    private async Task<string?> HouseholdDefaultAsync(ChannelMessage message, CancellationToken ct)
    {
        if (agentDefinitions is null)
        {
            return null;
        }

        try
        {
            return (await agentDefinitions.ForHouseholdAsync(message.Principal?.Account?.TenantId, ct)).DefaultAgent;
        }
        catch (Exception ex) when (ex is HttpRequestException or HouseholdConfigRefusedException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the default agent of the household of {ConversationId}",
                message.ConversationId);
            return null;
        }
    }

    private async IAsyncEnumerable<bool> ProcessChatThread(
        AgentKey agentKey,
        IAsyncGrouping<AgentKey, (IChannelConnection Channel, ChannelMessage Message)> group,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var conversation = new ConversationGroup(
            agentKey, channels, agentFactory, _targetResolver, threadResolver, metricsPublisher, memoryRecallHook,
            skillPreloader, assertions, delegations, standing, agentDefinitions, logger);

        await foreach (var turnUpdate in conversation.RunAsync(group, group.Complete, ct).WithCancellation(ct))
        {
            var deliveredContent = await _replyDispatcher.DeliverUpdateAsync(
                turnUpdate.Update, turnUpdate.Turn, ct);
            if (deliveredContent)
            {
                // Ends the span on the first delivered chunk; the scope publishes at most once, so
                // every later chunk of the same turn is a no-op. A turn that delivers nothing
                // never disposes it and records no first-reply latency.
                turnUpdate.Turn.FirstReply.Dispose();
            }

            yield return true;
        }
    }
}