using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Domain.Judgments;
using Domain.Metrics;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Domain.Tools.Web;
using Infrastructure.Agents.Mcp;
using Infrastructure.Agents.Skills;
using Infrastructure.Metrics;
using Infrastructure.Utils;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

public sealed class ToolApprovalChatClient : FunctionInvokingChatClient
{
    private readonly IToolApprovalHandler _approvalHandler;
    private readonly ToolPatternMatcher _patternMatcher;
    // Concurrent because it is read off the turn: a sandbox command's file operations ask it
    // through the bridge while another call's approval may be remembering a tool.
    private readonly ConcurrentDictionary<string, byte> _dynamicallyApproved;

    // The sites a call acting as the person was approved and remembered for, each with its tool.
    // Apart from the tools remembered above on purpose: those answer the exec bridge by tool name
    // (ToolPermission), and a yes for one site must never read there as a yes for the tool.
    private readonly ConcurrentDictionary<AsPersonSubject, byte> _approvedSubjects = new();
    private readonly IMetricsPublisher _metricsPublisher;
    private readonly string _conversationId;
    private readonly IToolInvocationObserver? _observer;
    private readonly IExecScreen? _execScreen;
    private readonly string? _agentId;
    private int _observed;

    // One line per request, found by the history the request's calls are all handed. Per request
    // rather than per client because a call can open a request of its own on a client like this
    // one — a worker — and its calls must not queue behind the call that is waiting for them.
    private readonly ConditionalWeakTable<IList<ChatMessage>, WrittenOrder> _writtenOrders = new();

    // Approved for every agent without a whitelist entry: loading a skill reads prose this repo
    // ships and changes nothing, and a load that stalled a turn on a prompt nobody sees would cost
    // the very round trip the skill exists to save (docs/adr/0039).
    private static readonly HashSet<string> _alwaysApproved =
        new([SkillsProvider.LoadToolName], StringComparer.OrdinalIgnoreCase);

    public ToolApprovalChatClient(
        IChatClient innerClient,
        IToolApprovalHandler approvalHandler,
        string conversationId,
        IEnumerable<string>? whitelistPatterns = null,
        IMetricsPublisher? metricsPublisher = null,
        IToolInvocationObserver? observer = null,
        IExecScreen? execScreen = null,
        string? agentId = null)
        : base(innerClient)
    {
        _observer = observer;
        _execScreen = execScreen;
        _agentId = agentId;
        ArgumentNullException.ThrowIfNull(approvalHandler);
        ArgumentNullException.ThrowIfNull(conversationId);
        _approvalHandler = approvalHandler;
        _patternMatcher = new ToolPatternMatcher(whitelistPatterns);
        _dynamicallyApproved = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        _metricsPublisher = metricsPublisher ?? NoOpMetricsPublisher.Instance;
        _conversationId = conversationId;

        IncludeDetailedErrors = true;
        MaximumIterationsPerRequest = 50;
        // Every call of a message is handed over at once; which of them actually run at once is
        // decided call by call, by what each says it touches (WrittenOrder).
        AllowConcurrentInvocation = true;
        MaximumConsecutiveErrorsPerRequest = 3;
    }

    protected override async ValueTask<object?> InvokeFunctionAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        // Before anything is awaited: a place in line is taken in the order calls are handed
        // over, and that is only the order they were written until the first of them waits.
        var place = _writtenOrders
            .GetValue(context.Messages, _ => new WrittenOrder())
            .Take(FootprintDeclaration.Of(context.Function, context.Arguments));
        try
        {
            return await DecideAsync(context, place, cancellationToken);
        }
        finally
        {
            // However the call ended — returned, threw, refused, cancelled — or the calls written
            // after it would wait for something that is never coming.
            place.GiveUp();
        }
    }

    private async ValueTask<object?> DecideAsync(
        FunctionInvocationContext context, WrittenOrder.Place place, CancellationToken cancellationToken)
    {
        var toolName = context.Function.Name;
        var request = new ToolApprovalRequest(
            context.Messages.LastOrDefault()?.MessageId,
            toolName,
            ToReadOnlyDictionary(context.CallContent.Arguments));

        // A call acting as the person is asked about by its site, whatever the whitelist or a
        // remembered tool says: the flag is the model's to set, and a page's own text can ask it
        // to, so nothing but the person's own yes for that site lets it run.
        if (AsPersonSubject.Of(toolName, request.Arguments) is { } subject && !IsUnpromptedFire(context.Options))
        {
            if (subject.Site is not null && _approvedSubjects.ContainsKey(subject))
            {
                return await RunUnaskedAsync(context, request, place, cancellationToken);
            }

            return await AskAsync(
                context, request with { Screen = [ExecScreenCodes.AsPerson] }, place, cancellationToken,
                remember: () =>
                {
                    // A call with no site has nothing to scope the yes to, so it is asked again.
                    if (subject.Site is not null)
                    {
                        _approvedSubjects.TryAdd(subject, 0);
                    }
                });
        }

        if (RunsUnasked(toolName))
        {
            // Every path that would run unasked is screened first — a remembered approval included,
            // so one tap cannot switch the screen off for the rest of the conversation. A flag is
            // not a refusal: it is the prompt a tool nobody whitelisted gets, saying why.
            if (await ScreenAsync(context, cancellationToken) is { Asks: true } flagged)
            {
                return await AskAsync(context, request with { Screen = flagged.Codes }, place, cancellationToken);
            }

            return await RunUnaskedAsync(context, request, place, cancellationToken);
        }

        return await AskAsync(context, request, place, cancellationToken);
    }

    // The notification is display-only; overlapping it with the invocation keeps a channel round
    // trip off the tool's critical path. A notify failure still surfaces, but no longer prevents
    // the tool from executing.
    private async ValueTask<object?> RunUnaskedAsync(
        FunctionInvocationContext context,
        ToolApprovalRequest request,
        WrittenOrder.Place place,
        CancellationToken cancellationToken)
    {
        var notifyTask = _approvalHandler.NotifyAutoApprovedAsync(
            _conversationId, [request], cancellationToken);
        var invokeTask = InvokeInTurnAsync(context, request.ToolName, place, cancellationToken).AsTask();
        await Task.WhenAll(notifyTask, invokeTask);
        return await invokeTask;
    }

    // A schedule's, a watch's or a finished download's turn has nobody present to ask: its
    // approvals go to the channel it delivers to, so asking there would leave a card on every
    // morning's fresh conversation. Such a fire runs as the whitelist says, and what bounds where
    // it may act as the person is the browse server's own rule. A login wake is not one of these:
    // it is the person's own conversation, just after they pressed Done.
    private static bool IsUnpromptedFire(ChatOptions? options) =>
        ConversationContextMeta.TryRead(options)?.OriginKind
            is MessageOriginKind.Schedule or MessageOriginKind.Watch or MessageOriginKind.Download;

    // A person is asked the moment the model wrote the call; what waits for the calls ahead is
    // the running, so the questions of one message still arrive together. Approve-and-remember
    // remembers the tool, unless the caller says what else the yes covers.
    private async ValueTask<object?> AskAsync(
        FunctionInvocationContext context,
        ToolApprovalRequest request,
        WrittenOrder.Place place,
        CancellationToken cancellationToken,
        Action? remember = null)
    {
        var toolName = request.ToolName;
        var result = await _approvalHandler.RequestApprovalAsync(
            _conversationId, [request], cancellationToken);

        switch (result)
        {
            case ToolApprovalResult.ApprovedAndRemember:
                (remember ?? (() => _dynamicallyApproved.TryAdd(toolName, 0)))();
                return await InvokeInTurnAsync(context, toolName, place, cancellationToken);

            case ToolApprovalResult.Approved:
            case ToolApprovalResult.AutoApproved:
                return await InvokeInTurnAsync(context, toolName, place, cancellationToken);

            case ToolApprovalResult.Rejected:
            default:
                context.Terminate = true;
                return $"Tool execution was rejected by user: {toolName}. Waiting for new input.";
        }
    }

    // The one question every unasked path answers: always approved, whitelisted, or remembered for
    // this conversation.
    private bool RunsUnasked(string toolName) =>
        _alwaysApproved.Contains(toolName) || _patternMatcher.IsMatch(toolName) || _dynamicallyApproved.ContainsKey(toolName);

    // Only a call whose function carries an ExecReach is screened — the exec a session built over
    // its own mounts — and only where the path lands on a mount with a shell. A call the person is
    // being asked about anyway never reaches here: asking is already what a flag would produce.
    private async Task<ExecScreenVerdict?> ScreenAsync(FunctionInvocationContext context, CancellationToken ct)
    {
        if (_execScreen is null
            || context.Function.GetService<ExecReach>() is not { } reach
            || CallArguments.Text(context.Arguments, "path") is not { } path
            || reach.Of(path, CallArguments.Text(context.Arguments, "command") ?? "") is not { } shellReach)
        {
            return null;
        }

        var asking = context.Messages.LastOrDefault(m => m.Role == ChatRole.User);

        return await _execScreen.ScreenAsync(
            new ExecScreenRequest(
                shellReach, CallArguments.Text(context.Arguments, "command") ?? "", path, context.Messages)
            {
                Caller = new JudgmentCaller(asking?.GetTurnModel(), asking?.GetSenderId(), _agentId, _conversationId)
            },
            ct);
    }

    // Pass-through in both directions: what the observer is handed is the option set the agent
    // built and the route the inner client ends up reporting, and nothing about the turn changes
    // because somebody is watching it.
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        ObserveTurn(options);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }

        // After the stream, not before it: the route is only known once a provider has answered.
        ObserveTurn(options);
    }

    private void ObserveTurn(ChatOptions? options) =>
        _observer?.OnTurn(new TurnObservation(
            options?.Instructions, GetService(typeof(ServedRoute)) as ServedRoute)
        { AgentId = _agentId });

    // Every call of one iteration passes through here, including the two that never reach
    // InvokeFunctionAsync: a call whose tool threw, and a call naming a tool nothing serves. That
    // is why the observation hangs off this override rather than off the invocation itself —
    // those two are exactly what an evaluation is hunting, and neither produces a result.
    protected override IList<ChatMessage> CreateResponseMessages(
        ReadOnlySpan<FunctionInvocationResult> results)
    {
        if (_observer is not null)
        {
            foreach (var result in results)
            {
                _observer.OnInvoked(
                    Describe(result, Interlocked.Increment(ref _observed) - 1) with { AgentId = _agentId });
            }
        }

        return base.CreateResponseMessages(results);
    }

    private static ToolInvocation Describe(FunctionInvocationResult result, int sequence) => new()
    {
        Sequence = sequence,
        ToolName = result.CallContent.Name,
        Arguments = SerializeArguments(result.CallContent.Arguments),
        Result = result.Result?.ToString(),
        Error = result.Exception?.Message,
        Outcome = result.Status switch
        {
            FunctionInvocationStatus.RanToCompletion => ToolInvocationOutcome.Completed,
            FunctionInvocationStatus.NotFound => ToolInvocationOutcome.NotFound,
            _ => ToolInvocationOutcome.Failed
        }
    };

    // Relaxed escaping, because the only reader of this string is a person reading a dump: the
    // default encoder turns every quote inside a nested document into \u0022 and the arguments
    // become unreadable exactly where they matter most.
    private static readonly JsonSerializerOptions _argumentJson =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        try
        {
            return JsonSerializer.Serialize(arguments ?? new Dictionary<string, object?>(), _argumentJson);
        }
        catch (NotSupportedException)
        {
            // An argument the serializer cannot write is still evidence the call happened, and an
            // observation must never be the thing that takes a turn down.
            return "{}";
        }
    }

    // Outside the measurement: the time a call spends behind another is that call's, not this
    // tool's.
    private async ValueTask<object?> InvokeInTurnAsync(
        FunctionInvocationContext context,
        string toolName,
        WrittenOrder.Place place,
        CancellationToken cancellationToken)
    {
        await place.TurnAsync(cancellationToken);
        return await InvokeWithMetricsAsync(context, toolName, cancellationToken);
    }

    private async ValueTask<object?> InvokeWithMetricsAsync(
        FunctionInvocationContext context,
        string toolName,
        CancellationToken cancellationToken)
    {
        // A tool call is measured whether it returned or threw, which is why this used to be the
        // same latency block twice. The scope publishes on both paths from one statement, and the
        // tool-call event reads its duration off the scope rather than a second stopwatch.
        using var latency = _metricsPublisher.MeasureLatency(LatencyStage.ToolExec, _conversationId);

        // On the call's own arguments, so a tool acting for this conversation without a model call
        // of its own — the exec bridge — asks exactly what this client would decide, at the moment
        // it asks: an approval remembered mid-command counts from then on.
        context.Arguments.Context ??= new Dictionary<object, object?>();
        context.Arguments.Context[ToolPermission.ContextKey] = new ToolPermission(RunsUnasked);
        // And the conversation the call serves, for the same tools: what they do outside the turn
        // still has to say who is calling.
        context.Arguments.Context[ConversationContext.ContextKey] = ConversationContextMeta.TryRead(context.Options);
        try
        {
            var result = await base.InvokeFunctionAsync(context, cancellationToken);
            var (isError, errorMessage) = DetectError(result);
            _metricsPublisher.Publish(new ToolCallEvent
            {
                ToolName = toolName,
                DurationMs = latency.ElapsedMilliseconds,
                Success = !isError,
                Error = errorMessage,
                ConversationId = _conversationId
            });
            return result;
        }
        catch (Exception ex)
        {
            _metricsPublisher.Publish(new ToolCallEvent
            {
                ToolName = toolName,
                DurationMs = latency.ElapsedMilliseconds,
                Success = false,
                Error = ex.Message,
                ConversationId = _conversationId
            });
            throw;
        }
    }

    // Both checks are required, not redundant:
    //   - MCP tool results carry the envelope inside `content` AND have `isError:true` at the
    //     protocol level (set by ToolResponse.Create(Exception/JsonNode) at the boundary).
    //   - In-process Domain tool invocations return the envelope directly with no `isError`
    //     wrapper, so we still need the `ok:false` check to catch those.
    private static (bool IsError, string? Message) DetectError(object? result)
    {
        if (result is not JsonElement { ValueKind: JsonValueKind.Object } json)
        {
            return (false, null);
        }

        if (json.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            return (true, json.TryGetProperty("content", out var content) ? content.ToString() : null);
        }

        if (json.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            return (true, json.TryGetProperty("message", out var message) ? message.GetString() : null);
        }

        return (false, null);
    }

    private static IReadOnlyDictionary<string, object?> ToReadOnlyDictionary(IDictionary<string, object?>? source)
    {
        return source as IReadOnlyDictionary<string, object?>
               ?? new Dictionary<string, object?>(source ?? new Dictionary<string, object?>());
    }
}