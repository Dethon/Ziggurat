using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// One run of one scenario: a fresh stack, one turn, one recording. Built per run rather than
// shared, because a scenario's subject is what the agent does with an empty conversation and a
// pinned clock, and a stack carried over from the previous run has neither.
public static class EvalRun
{
    // The account every scenario runs as, by its user id. Memory is keyed by it, and the forget
    // tool refuses a turn that acts for no account — so it is one constant rather than a string
    // repeated per fixture.
    public const string UserId = "eval-user";

    // The household that account belongs to: nothing in a run is keyed by it, but an account
    // always names one.
    public const string Tenant = "eval-home";

    // The account that user signed in as, which every turn's principal carries as a signed-in
    // channel stamps it: what follows the person — their memory, their Sugary log — is found by it.
    public static readonly Account Account = new(Tenant, UserId);

    // One run in a slot of its own. The gate is what bounds how many stacks stand at once now
    // that a scenario's runs go out together, and the lease is the database this run is alone on.
    public static Task<Recording> ExecuteAsync(Scenario scenario) =>
        EvalConcurrency.Gate.RunAsync(async () =>
        {
            await using var redis = await EvalRedis.LeaseAsync();
            return await ExecuteAsync(scenario, redis.ConnectionString);
        });

    public static async Task<Recording> ExecuteAsync(Scenario scenario, string redisConnectionString)
    {
        var conversationId = $"eval:{Guid.NewGuid():N}";

        // Named so the recording can tell this agent's calls from a real worker's: both are built
        // by the one factory and heard by the one observer. Both of its names — the one its
        // metrics carry and the one its session does, which is the display name the factory
        // composes from the definition's name and the conversation.
        var recording = new Recording
        {
            Parent = ShippedDefinition.Repository.Settings.Agents
                .Where(agent => agent.Id == scenario.AgentId)
                .SelectMany(agent => new[] { agent.Name, $"{agent.Name}-{conversationId}" })
                .ToList()
        };
        await using var stack = await EvalStack.StartAsync(
            scenario, redisConnectionString, recording);

        await using var agent = stack.Factory.Create(
            new AgentKey(conversationId, scenario.AgentId),
            userId: UserId,
            agentId: scenario.AgentId,
            approvalHandler: new AutoApproveHandler());

        // Taken after the stack is up and before the turn goes out, so what the diff reports is
        // what this turn did rather than what arming the scenario did.
        recording.StateBefore = stack.Home.Snapshot();
        recording.FilesBefore = EvalVault.Read(stack.VaultPath);

        // A stalled provider costs the run rather than the suite. The scenario's own budget, and
        // generous by default: a run cut off at its deadline is reported as never having
        // finished, which is a behavioural failure only if the time was enough.
        using var cancellation = new CancellationTokenSource(scenario.Budget);
        var thread = await agent.CreateSessionAsync(cancellation.Token);

        // A deadline reached is a result, not an accident: the scenario keeps its row and says
        // the turn never finished. Throwing here dropped the scenario off the scorecard entirely,
        // which reads as a scenario nobody ran rather than one the model could not finish.
        // The turn alone, as the standalone benchmark timed an estimate: the stack standing up is
        // the harness's time, not the person's wait.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var messages = Messages(scenario, conversationId, stack.Memory.Context);
            await LandAsync(agent, thread, messages[^1], stack.Attachments, conversationId, cancellation.Token);
            var response = await agent.RunAsync(messages, thread, cancellationToken: cancellation.Token);

            recording.Reply = response.Text;
            recording.Answer = Answer(response.Messages);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException
                                   && cancellation.IsCancellationRequested)
        {
            recording.TimedOut = true;
        }
        // The provider refusing the turn — an HTTP 5xx from the router, a dropped connection —
        // is the same kind of result: a run that never finished, kept on the scorecard with its
        // reason rather than thrown past it. A harness bug is not one of these and still throws.
        catch (Exception ex) when (ex is System.ClientModel.ClientResultException or HttpRequestException)
        {
            recording.ProviderError = ex.Message.ReplaceLineEndings(" ").Trim();
        }
        recording.Elapsed = clock.Elapsed;
        recording.AttachmentsServed = stack.Attachments.Served;
        recording.StateAfter = stack.Home.Snapshot();
        recording.FilesAfter = EvalVault.Read(stack.VaultPath);
        recording.Delegations = scenario.Workers == WorkerMode.Real
            ? Delegation.Heard(recording.Calls)
            : stack.Workers.Delegations;
        recording.MemoriesAfter = stack.Memory.Remaining;
        return recording;
    }

    // The whole run's input: the scripted history, oldest first and five minutes apart, then the
    // turn itself. History rides as ordinary prior messages on the turn's own channel — a user
    // message decorated the way the channel decorates one, and the assistant's answer verbatim —
    // so what a multi-turn scenario tests is a conversation the model actually sees.
    public static IReadOnlyList<ChatMessage> Messages(
        Scenario scenario, string conversationId, MemoryContext? memories = null) =>
    [
        .. scenario.History.SelectMany<HistoryExchange, ChatMessage>((exchange, index) =>
        [
            Decorate(new ChatMessage(ChatRole.User, exchange.User), scenario,
                scenario.Instant.AddMinutes(-5 * (scenario.History.Count - index))),
            new ChatMessage(ChatRole.Assistant, exchange.Assistant)
        ]),
        Turn(scenario, conversationId, memories)
    ];

    // Everything a channel puts on a turn, set the way a channel sets it. The decoration itself
    // happens where it happens in production — on the way out of the chat client — so what this
    // builds is the message, never the prefix.
    public static ChatMessage Turn(
        Scenario scenario, string conversationId, MemoryContext? memories = null)
    {
        var message = Decorate(
            new ChatMessage(ChatRole.User, scenario.Turn.Text), scenario, scenario.Instant);
        message.SetDismissedAlert(scenario.Turn.DismissedAlert);
        // As ConversationGroup stamps a channel's message: the format for this turn alone, and the
        // files as references of the channel that holds them — never bytes on the turn (ADR-0020).
        message.SetResponseFormat(scenario.Turn.ResponseFormat);
        var attachments = EvalAttachments.References(scenario.Turn);
        message.SetAttachments(attachments.Count > 0 ? attachments : null);
        message.SetAttachmentChannelId(attachments.Count > 0 ? EvalAttachments.Channel(scenario.Turn) : null);
        // Set on the message the recall hook would have set it on, so the block reaches the model
        // through the decoration that renders it in production rather than through a second path.
        message.SetMemoryContext(memories);
        // Every turn acts for the eval's one account, as a channel on accounts stamps a person's turn
        // and a fire carries the principal of the turn that wrote it — an eval's fire stands in for
        // one the person wrote from the channel it delivers to. Memory is keyed by the account, so
        // its user id is the one the remembered facts are filed under.
        var channel = EvalAttachments.Channel(scenario.Turn);
        message.SetConversationContext(new ConversationContext(
            scenario.AgentId, conversationId, UserId,
            new ReplyTarget(channel, conversationId, scenario.Turn.SatelliteId),
            OriginKind: scenario.Turn.Origin,
            Principal: new Principal(channel, UserId, Account)));

        return message;
    }

    // The turn's files put in the sandbox as ConversationGroup puts a channel's there: after the
    // session is built, because the mounts only exist then, and recorded on the message rather than
    // written into it. Without it no scenario sends what the model is told about a landed file, and
    // that note once sent jonas to file_read a photo it could already see (2026-10-18). An agent
    // with no sandbox lands nothing, as in the deployment.
    private static async Task LandAsync(
        DisposableAgent agent, AgentSession thread, ChatMessage turn, EvalAttachments attachments,
        string conversationId, CancellationToken ct)
    {
        if (turn.GetAttachments() is not { Count: > 0 } references)
        {
            return;
        }

        await agent.WarmupSessionAsync(thread, ct);
        var landed = await AttachmentLanding.LandAsync(
            new AttachmentLanding.Landing(
                agent.GetFileSystemRegistry(thread), references, attachments.ReadAsync, conversationId,
                Guid.NewGuid().ToString("N")),
            NullLogger.Instance,
            ct);
        turn.SetSandboxPaths(landed.Landed);
        turn.SetLandingFailures(landed.Failed);
    }

    // What a channel reads as the answer: the last message that said anything. The Sugary channel
    // collects text per message and parses the last one, so a line written before a tool call is
    // never read as the estimate — and the benchmark reads the same message the app would.
    public static string? Answer(IEnumerable<ChatMessage> messages) =>
        messages
            .LastOrDefault(message => message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text))
            ?.Text;

    private static ChatMessage Decorate(ChatMessage message, Scenario scenario, DateTimeOffset at)
    {
        message.SetSenderId(scenario.Turn.Sender);
        message.SetLocation(scenario.Turn.Room);
        message.SetSatelliteId(scenario.Turn.SatelliteId);
        message.SetTimestamp(at);
        return message;
    }

    // What the dump shows: the same function the client applies, run against the same message and
    // the same zone, so what a diagnosis reads is what the model read.
    public static string Decorated(Scenario scenario) =>
        TurnDecoration
            .Apply(
                Turn(scenario, "eval:dump", new EvalMemory(scenario.Remembered).Context),
                TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"))
            .Text;
}

// Nothing in an eval is approved by a person, and the whitelist a scenario runs under is the
// shipped one — so what this stands in for is the channel that would have asked, never the rule
// that decides whether asking is needed.
public sealed class AutoApproveHandler : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(ApprovalAsk ask, CancellationToken ct) =>
        Task.FromResult(ToolApprovalResult.Approved);

    public Task NotifyAutoApprovedAsync(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct) =>
        Task.CompletedTask;
}