using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Domain.Metrics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

// Everything one conversation and one agent own for the length of the group: the pending-turn
// queue, the command dispatch, the delivery anchors, the agent, the restored thread and the
// session warmup. The order they are established in is the order of the statements below, so
// changing it is a local edit rather than an argument spread across ChatMonitor.
internal sealed class ConversationGroup(
    AgentKey agentKey,
    IAgentFactory agentFactory,
    DeliveryTargetResolver targetResolver,
    ChatThreadResolver threadResolver,
    IMetricsPublisher metricsPublisher,
    IMemoryRecallHook? memoryRecallHook,
    ILogger logger) : IAsyncDisposable
{
    // The outer token: it ends with the monitor, not with a turn. The two establishing stages
    // that reach outside — minting the delivery targets and restoring the thread — run on it
    // rather than on the per-turn token, so a /cancel arriving mid-establish lets them finish
    // instead of leaving a conversation minted on one channel and unknown to the group, or a
    // half-read thread. The turn is dropped all the same: the warmup does start on the turn
    // token, so the wait on it raises the cancellation that RunTurnsSequentiallyAsync absorbs
    // to end the group.
    private CancellationToken _groupCt;

    // The group token: the outer one linked with the thread context's, so a /cancel or /clear
    // ends the turns.
    private CancellationToken _turnCt;

    private GroupState? _state;

    // The one this group resolved. Cancelling is addressed at it, so a group that fails after
    // a /cancel already replaced it under the same key cannot tear its successor down.
    private ChatThreadContext? _context;

    // Set once a turn has waited on the warmup, so its failure has already been reported as
    // that turn's failure.
    private bool _warmupSurfaced;

    // Held from the moment it is created, not from the moment the group is established, so a
    // failure between the two still disposes it.
    private DisposableAgent? _agent;

    private sealed record GroupState(
        ChannelMessage AnchorMessage,
        IReadOnlyList<DeliveryTarget> Targets,
        AgentKey DeliveryKey,
        DisposableAgent Agent,
        AgentSession Thread,
        Task Warmup);

    public async IAsyncEnumerable<TurnUpdate> RunAsync(
        IAsyncGrouping<AgentKey, (IChannelConnection Channel, ChannelMessage Message)> messages,
        Action onGroupComplete,
        [EnumeratorCancellation] CancellationToken ct)
    {
        _groupCt = ct;

        // The context is what a /clear or /cancel disposes to end the group, and it carries the
        // completion callback and the turn token, so it exists before any message is read: a
        // command that found no live context would leave this group running against a context
        // resolved after it.
        var linked = TrySetUpContext(onGroupComplete);
        if (linked is null)
        {
            yield break;
        }

        using var linkedCts = linked;
        _turnCt = linkedCts.Token;

        await foreach (var update in RunTurnsSequentiallyAsync(messages))
        {
            yield return update;
        }
    }

    // The same protection the turn loop gives a half-built group, one stage earlier. Setting the
    // context up is the first thing the group does, and every step of it throws: resolving,
    // once the container has disposed the resolver at shutdown; registering the callback and
    // linking the token, on a context disposed between resolving it and using it — the same
    // shutdown race one step later, and the /cancel that replaced this group's context while it
    // was starting. Thrown out of RunAsync any of them would be swallowed by the monitor's
    // stream merge, leaving the grouping never completed and every later message for this
    // conversation queued into a group nobody reads. Completing the grouping here ends it, so a
    // message arriving after a resolver that is up again opens a fresh group.
    private CancellationTokenSource? TrySetUpContext(Action onGroupComplete)
    {
        try
        {
            var context = threadResolver.Resolve(agentKey);
            context.RegisterCompletionCallback(onGroupComplete);
            var linked = context.GetLinkedTokenSource(_groupCt);
            _context = context;
            return linked;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Setting up the thread context failed for conversation {ConversationId} and agent {AgentId}; ending the group",
                agentKey.ConversationId, agentKey.AgentId);
            onGroupComplete();
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_agent is not null)
        {
            await _agent.DisposeAsync();
        }
    }

    // One turn at a time per group key — the (ConversationId, AgentId) of the incoming message.
    // Three pieces of state shared across a group's turns depend on this and are not defended
    // anywhere else: ToolApprovalChatClient's dynamically-approved tool set (an unsynchronised
    // HashSet mutated mid-turn), and OpenRouterChatClient's reasoning queue and cost/cached-token
    // queues (drained per update and per response, so two interleaved streams on one client
    // cross-attribute each other's values). Reintroducing concurrency here re-breaks all
    // three. Different group keys and the fan-out across delivery targets stay concurrent.
    //
    // The key is the message's own conversation, not the one the replies land in, so this is
    // not "one turn at a time per conversation": a schedule fire keyed on its synthetic id and
    // the user typing in the WebChat conversation it delivers into are two groups, and their
    // turns run concurrently against that one conversation and its persisted thread. The three
    // pieces of state above survive that because each group builds its own agent, and with it
    // its own chat client stack.
    //
    // Commands do NOT queue: /cancel is how the stop button reaches the monitor, so it has to
    // reach threadResolver while the turn it stops is still running. /clear is immediate for
    // the same reason — the user is discarding the thread, so the running turn must stop
    // writing into it and a queued turn must not run against it; the teardown acknowledges
    // what it drops. Reading messages in a separate loop keeps commands immediate and turns
    // sequential.
    private async IAsyncEnumerable<TurnUpdate> RunTurnsSequentiallyAsync(
        IAsyncGrouping<AgentKey, (IChannelConnection Channel, ChannelMessage Message)> messages)
    {
        var pending = Channel.CreateUnbounded<(IChannelConnection Channel, ChannelMessage Message)>();
        // The message pump is stopped on the way out. Without that, a turn that throws would
        // wait here for a message stream that has no reason to end, and the error would never
        // reach the monitor.
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(_turnCt);
        var reader = DispatchCommandsAndQueueTurnsAsync(messages, pending.Writer, pumpCts.Token);

        // Enumerated by hand rather than with await foreach, because reading is itself a
        // failure site: the pump folds anything it throws — a wire message that deserialized
        // with no content, say — into the channel, and it comes back out here. Out of an
        // await foreach that fault would pass both per-turn catches and leave RunAsync, where
        // the monitor's stream merge swallows it and the group stays registered and unread. It
        // ends the group instead, exactly like a turn that fails to set up.
        var queue = pending.Reader.ReadAllAsync(_turnCt).IgnoreCancellation(_turnCt).GetAsyncEnumerator();

        try
        {
            while (true)
            {
                bool hasTurn;
                try
                {
                    hasTurn = await queue.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "The message pump failed for conversation {ConversationId} and agent {AgentId}; ending the group",
                        agentKey.ConversationId, agentKey.AgentId);
                    CancelOwnContext();
                    await ObserveAbandonedWarmupAsync();
                    break;
                }

                if (!hasTurn)
                {
                    break;
                }

                var x = queue.Current;
                // The reader hands over items it already buffered even after the token fired,
                // so a turn queued behind the one a /clear or /cancel just ended would start
                // against the torn-down group. It is dropped instead, and acknowledged below.
                if (_turnCt.IsCancellationRequested)
                {
                    LogDroppedTurn(logger, x.Message);
                    break;
                }

                IAsyncEnumerable<TurnUpdate> turn;
                try
                {
                    turn = await RunTurnAsync(x);
                }
                catch (OperationCanceledException) when (_turnCt.IsCancellationRequested)
                {
                    // A /cancel or /clear mid-setup: the context dispose that raised this
                    // already completed the group. The filter is what tells that apart from a
                    // cancellation nobody asked for — the establishing stages run on the group
                    // token, and an HttpClient timeout inside minting a conversation or
                    // restoring the thread arrives as a TaskCanceledException. Without it that
                    // timeout would break out of here with the group never completed, leaving
                    // every later message queued into a channel nobody reads.
                    await ObserveAbandonedWarmupAsync();
                    break;
                }
                catch (Exception ex)
                {
                    // A turn that fails to set up — the state store down while restoring the
                    // thread, say — must not leave the group half-built: an exception thrown
                    // out of here is swallowed by the monitor's stream merge, the group would
                    // stay registered, and every later message for this conversation would
                    // queue into it unread until restart. Cancelling the context completes
                    // the group, so the next message opens a fresh one.
                    logger.LogError(ex,
                        "Turn setup failed for conversation {ConversationId} and agent {AgentId}; ending the group",
                        agentKey.ConversationId, agentKey.AgentId);
                    CancelOwnContext();
                    await ObserveAbandonedWarmupAsync();
                    break;
                }

                await foreach (var update in turn.IgnoreCancellation(_turnCt))
                {
                    yield return update;
                }
            }
        }
        finally
        {
            await queue.DisposeAsync();
            await pumpCts.CancelAsync();
            await reader;
            // Anything still queued when the group ends is dropped by that end — a /clear or
            // /cancel dispatched ahead of it, a setup failure, a shutdown. Dropping is the
            // decided semantics (the command's immediacy is its point); vanishing silently is
            // not, so each dropped turn is named — whether the pump had already queued it or
            // it still sits unread in the grouping's channel. The pump is done, so between
            // them these two drains cover everything.
            while (pending.Reader.TryRead(out var dropped))
            {
                LogDroppedTurn(logger, dropped.Message);
            }

            foreach (var (_, message) in messages.DrainPending())
            {
                LogDroppedTurn(logger, message);
            }
        }
    }

    // A setup that exits before the deterministic wait on the warmup — the /cancel that
    // cancelled it, or an earlier stage failing — leaves the fire-and-forget warmup with no
    // one to observe it. Its cancellation is just the teardown arriving there too; anything
    // else is a real session failure that would otherwise die as an unobserved task exception.
    private async Task ObserveAbandonedWarmupAsync()
    {
        if (_state is null)
        {
            return;
        }

        try
        {
            await _state.Warmup;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (!_warmupSurfaced)
        {
            logger.LogError(ex,
                "Session warmup failed for conversation {ConversationId} and agent {AgentId}",
                agentKey.ConversationId, agentKey.AgentId);
        }
        catch (Exception)
        {
            // The turn already waited on this warmup, so its failure came out as the turn's
            // own setup failure and was logged there. Awaiting it again is only about not
            // leaving an unobserved task exception behind.
        }
    }

    internal static void LogDroppedTurn(ILogger logger, ChannelMessage message)
    {
        logger.LogWarning(
            "Group teardown dropped a queued turn for conversation {ConversationId} and agent {AgentId}",
            message.ConversationId, message.AgentId);
    }

    private async Task DispatchCommandsAndQueueTurnsAsync(
        IAsyncEnumerable<(IChannelConnection Channel, ChannelMessage Message)> messages,
        ChannelWriter<(IChannelConnection Channel, ChannelMessage Message)> writer,
        CancellationToken pumpCt)
    {
        try
        {
            await foreach (var x in messages.IgnoreCancellation(pumpCt))
            {
                switch (ChatCommandParser.Parse(x.Message.Content))
                {
                    case ChatCommand.Clear:
                        await ClearThreadAsync();
                        break;
                    case ChatCommand.Cancel:
                        CancelOwnContext();
                        break;
                    default:
                        // TryWrite, not WriteAsync with the pump token: on an unbounded
                        // channel it always lands, so a message read just as teardown
                        // cancels the pump still reaches the pending queue, where the
                        // teardown drain names it instead of losing it to a cancelled write.
                        writer.TryWrite(x);
                        break;
                }
            }

            writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    // ClearAsync disposes the live context before it deletes the persisted state — that
    // order is load-bearing: a message arriving during a slow delete must open a fresh
    // group, not join this dying one. The cost is that a failed delete surfaces after the
    // group already completed, when the reader is exiting on the cancellation the dispose
    // raised and never observes a fault folded into the pending channel. So the failure is
    // named here, at the only site that still sees it: the live thread is gone, the
    // persisted one survived, and the cleared history returns on the next message.
    private void CancelOwnContext()
    {
        if (_context is not null)
        {
            threadResolver.Cancel(agentKey, _context);
        }
    }

    private async Task ClearThreadAsync()
    {
        try
        {
            await threadResolver.ClearAsync(agentKey);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Wiping persisted thread state failed for conversation {ConversationId} and agent {AgentId}; the cleared history returns on the next message",
                agentKey.ConversationId, agentKey.AgentId);
        }
    }

    // Resolve delivery targets BEFORE anything downstream, because the turn's whole
    // identity comes out of them. The delivery identity is the first delivery target's
    // conversation id, or the message's own when nothing resolved, and it is the id
    // everything the turn produces is filed under: the agent is built from it, chat
    // history restores under it, approvals route to it, and every event it publishes is
    // stamped with it. The rule is "name the conversation the reply actually landed in".
    // A schedule fire delivers into a minted WebChat conversation, so filing any of that
    // under the synthetic scheduling id would name a conversation nobody can open — and
    // for chat history it is worse than a label: WebChat reads history keyed on the
    // minted id and would see an empty conversation.
    //
    // The approval channel follows the same anchor, not the origin. Schedule/ServiceBus
    // channels auto-approve silently, so binding approvals to the origin would hide tool
    // calls from the user in WebChat.
    //
    // These targets anchor the group; per-message reply delivery is resolved separately in
    // ResolveTurnTargetsAsync.
    //
    // None of it is built until the group has a turn to run. A chat command is never queued
    // and never answered, so a group whose messages are all commands — a /clear on a
    // conversation with no live group, routine after a restart — resolves no target, mints no
    // conversation, builds no agent, reads no thread and opens no MCP connection.
    private async Task<GroupState> EnsureEstablishedAsync(
        (IChannelConnection Channel, ChannelMessage Message) x)
    {
        if (_state is not null)
        {
            return _state;
        }

        var targets = await targetResolver.ResolveAsync(x.Message, x.Channel, _groupCt);
        var (approvalChannel, deliveryKey) = targets.Count > 0
            ? (targets[0].Channel, new AgentKey(targets[0].ConversationId, x.Message.AgentId))
            : (x.Channel, agentKey);
        var agent = _agent = agentFactory.Create(
            deliveryKey, x.Message.Sender, x.Message.AgentId, approvalChannel);
        var thread = await GetOrRestoreThread(agent, deliveryKey);

        // Start session warmup (MCP connections + tool discovery) without awaiting it yet, so
        // it overlaps the turn-start announce and memory recall. It is awaited deterministically
        // just before the first RunStreamingAsync, so it never outlives the agent and the order
        // of operations is well-defined.
        var warmup = agent.WarmupSessionAsync(thread, _turnCt);

        return _state = new GroupState(x.Message, targets, deliveryKey, agent, thread, warmup);
    }

    private async Task<IAsyncEnumerable<TurnUpdate>> RunTurnAsync(
        (IChannelConnection Channel, ChannelMessage Message) x)
    {
        var state = await EnsureEstablishedAsync(x);
        var targets = await ResolveTurnTargetsAsync(x, state);
        // FirstReply times the turn from the moment it starts to its first delivered reply
        // chunk: memory recall, the wait on session warmup and the turn-start announce for
        // agent-initiated messages. It is the turn's own window rather than the user's wall
        // clock — neither the queue wait behind a running turn nor establishing the group on
        // its first turn is measured, and per-turn target resolution (an in-memory routing
        // decision on every path; the minting resolution happened while establishing) opens
        // the window rather than sitting inside it, so the scope can carry the turn's own
        // anchor: the conversation this turn's reply actually lands in, which for a later
        // interactive turn is its own origin rather than the group's delivery key.
        var firstReply = metricsPublisher.MeasureLatency(
            LatencyStage.FirstReply,
            targets.Count > 0 ? targets[0].ConversationId : agentKey.ConversationId);
        // A channel that has to recognise its own answer mints the key itself, before it dispatches
        // (voice does). Everything else arrives without one, and gets one here, so from this point
        // on every turn has a key whichever channel the message came from.
        var turn = new Turn(
            x.Message, targets, firstReply, x.Message.TurnKey ?? TurnKey.Mint());
        // Agent-initiated turns (downloads, schedules) land in conversations with no live
        // stream on the receiving channel; announce the turn so the channel can set one up
        // before reply chunks arrive.
        if (turn.Message.Origin is not null)
        {
            await targetResolver.AnnounceTurnStartAsync(turn.Targets, turn.Message, _turnCt);
        }
        var userMessage = await BuildUserMessageAsync(turn, state);

        // From here a warmup failure is this turn's failure and is reported as one, so the
        // abandoned-warmup observer must not name it a second time.
        _warmupSurfaced = true;
        await state.Warmup;
        // After the warmup, because the mounts only exist once the session has been built. An
        // agent with no sandbox lands nothing and keeps the attachment as model context, which is
        // the whole feature for that agent.
        await LandAttachmentsAsync(x.Channel, turn, state, userMessage);
        return StreamAgentTurn(state, userMessage, turn);
    }

    private async Task LandAttachmentsAsync(
        IChannelConnection channel, Turn turn, GroupState state, ChatMessage userMessage)
    {
        if (turn.Message.Attachments is not { Count: > 0 } attachments)
        {
            return;
        }

        var landed = await AttachmentLanding.LandAsync(
            state.Agent.GetFileSystemRegistry(state.Thread),
            attachments,
            (id, ct) => channel.FetchAttachmentAsync(id, ct),
            state.DeliveryKey.ConversationId,
            turn.TurnKey,
            logger,
            _turnCt);

        // Recorded on the message rather than written into its text: the model is told on the
        // way out, by the same step that puts the bytes back, so the transcript a person reads
        // never grows an internal path.
        userMessage.SetSandboxPaths(landed);
    }

    // Deliver each message's reply to the channel that actually sent it. The group is keyed
    // only by (ConversationId, AgentId), so a later message from a different channel — e.g.
    // the user typing in WebChat inside a voice-started conversation — joins this same group.
    // The group anchors cover the initiating message and any ReplyTo fan-out (re-resolving
    // the latter would re-mint conversations); a subsequent plain interactive message is
    // routed back to its own origin instead of the opening channel.
    private async Task<IReadOnlyList<DeliveryTarget>> ResolveTurnTargetsAsync(
        (IChannelConnection Channel, ChannelMessage Message) x, GroupState state)
    {
        // The anchors belong to the turn they were resolved from — the group's first — and
        // that is answered by identity, not by counting messages.
        if (ReferenceEquals(x.Message, state.AnchorMessage))
        {
            return state.Targets;
        }

        // A later turn reusing the group targets minted nothing of its own, so the marker
        // is cleared: those conversations pre-exist this turn and the announce has to set
        // their streams up again.
        return x.Message.ReplyTo is { Count: > 0 }
            ? [.. state.Targets.Select(t => t with { Minted = false })]
            : await targetResolver.ResolveAsync(x.Message, x.Channel, _turnCt);
    }

    private async Task<ChatMessage> BuildUserMessageAsync(Turn turn, GroupState state)
    {
        var message = turn.Message;
        var userMessage = new ChatMessage(ChatRole.User, message.Content);
        userMessage.SetSenderId(message.Sender);
        userMessage.SetLocation(message.Location);
        userMessage.SetSatelliteId(message.SatelliteId);
        userMessage.SetDismissedAlert(message.DismissedAlert);
        userMessage.SetConfigPatch(message.ConfigPatch);
        // References only. The bytes rest wherever the channel keeps them and are put back on the
        // way to the model, so a history read costs the same whether or not files were sent
        // (ADR 0020). The channel id rides along because it names who still holds them.
        userMessage.SetAttachments(message.Attachments);
        userMessage.SetAttachmentChannelId(
            message.Attachments is { Count: > 0 } ? message.ChannelId : null);
        userMessage.SetTimestamp(DateTimeOffset.UtcNow);
        userMessage.SetConversationContext(
            DeliveryTargetResolver.BuildConversationContext(message, turn.Targets));
        if (memoryRecallHook is not null)
        {
            // The delivery identity again, not the message's own: recall stamps durable
            // provenance on any memory extracted from this turn, so the source it names
            // has to be a conversation that can still be opened.
            await memoryRecallHook.EnrichAsync(
                userMessage, message.Sender, state.DeliveryKey.ConversationId, message.AgentId, state.Thread, _turnCt);
        }

        return userMessage;
    }

    private IAsyncEnumerable<TurnUpdate> StreamAgentTurn(GroupState state, ChatMessage userMessage, Turn turn)
    {
        var stopwatch = Stopwatch.StartNew();
        var ct = _turnCt;
        return state.Agent
            .RunStreamingAsync([userMessage], state.Thread, cancellationToken: ct)
            .WithErrorHandling(ct)
            .ToUpdateAiResponsePairs()
            .Append((new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null))
            .OnCompletion(
                seed: false,
                fold: (faulted, pair) => faulted || pair.Item1.Contents.OfType<ErrorContent>().Any(),
                onCompletion: (faulted, _) =>
                {
                    var error = faulted ? "Agent run reported an error" : null;
                    var evt = ScheduleExecutionEvent.FromMessage(
                        turn.Message, stopwatch.ElapsedMilliseconds, !faulted, error);
                    if (evt is not null)
                    {
                        metricsPublisher.Publish(evt);
                    }

                    return ValueTask.CompletedTask;
                },
                ct)
            .Select(pair => new TurnUpdate(pair.Item1, turn));
    }

    private ValueTask<AgentSession> GetOrRestoreThread(DisposableAgent agent, AgentKey deliveryKey)
    {
        return agent.DeserializeSessionAsync(
            JsonSerializer.SerializeToElement(deliveryKey.ToString()), null, _groupCt);
    }
}