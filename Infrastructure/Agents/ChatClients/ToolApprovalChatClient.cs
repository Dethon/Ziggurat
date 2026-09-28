using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Domain.Metrics;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.Skills;
using Infrastructure.Metrics;
using Infrastructure.Utils;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

public sealed class ToolApprovalChatClient : FunctionInvokingChatClient
{
    private readonly IToolApprovalHandler _approvalHandler;
    private readonly ToolPatternMatcher _patternMatcher;
    private readonly HashSet<string> _dynamicallyApproved;
    private readonly IMetricsPublisher _metricsPublisher;
    private readonly string _conversationId;
    private readonly IToolInvocationObserver? _observer;
    private readonly IExecScreen? _execScreen;
    private readonly string? _agentId;
    private int _observed;

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
        _dynamicallyApproved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _metricsPublisher = metricsPublisher ?? NoOpMetricsPublisher.Instance;
        _conversationId = conversationId;

        IncludeDetailedErrors = true;
        MaximumIterationsPerRequest = 50;
        AllowConcurrentInvocation = true;
        MaximumConsecutiveErrorsPerRequest = 3;
    }

    protected override async ValueTask<object?> InvokeFunctionAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        var toolName = context.Function.Name;
        var request = new ToolApprovalRequest(
            context.Messages.LastOrDefault()?.MessageId,
            toolName,
            ToReadOnlyDictionary(context.CallContent.Arguments));

        if (_alwaysApproved.Contains(toolName) || _patternMatcher.IsMatch(toolName) || _dynamicallyApproved.Contains(toolName))
        {
            // Every path that would run unasked is screened first — a remembered approval included,
            // so one tap cannot switch the screen off for the rest of the conversation. A flag is
            // not a refusal: it is the prompt a tool nobody whitelisted gets, saying why.
            if (await ScreenAsync(context, cancellationToken) is { Asks: true } flagged)
            {
                return await AskAsync(context, request with { Screen = flagged.Codes }, cancellationToken);
            }

            // The notification is display-only; overlapping it with the invocation keeps a
            // channel round trip off the tool's critical path. A notify failure still
            // surfaces, but no longer prevents the tool from executing.
            var notifyTask = _approvalHandler.NotifyAutoApprovedAsync(
                _conversationId, [request], cancellationToken);
            var invokeTask = InvokeWithMetricsAsync(context, toolName, cancellationToken).AsTask();
            await Task.WhenAll(notifyTask, invokeTask);
            return await invokeTask;
        }

        return await AskAsync(context, request, cancellationToken);
    }

    private async ValueTask<object?> AskAsync(
        FunctionInvocationContext context, ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var toolName = request.ToolName;
        var result = await _approvalHandler.RequestApprovalAsync(
            _conversationId, [request], cancellationToken);

        switch (result)
        {
            case ToolApprovalResult.ApprovedAndRemember:
                _dynamicallyApproved.Add(toolName);
                return await InvokeWithMetricsAsync(context, toolName, cancellationToken);

            case ToolApprovalResult.Approved:
            case ToolApprovalResult.AutoApproved:
                return await InvokeWithMetricsAsync(context, toolName, cancellationToken);

            case ToolApprovalResult.Rejected:
            default:
                context.Terminate = true;
                return $"Tool execution was rejected by user: {toolName}. Waiting for new input.";
        }
    }

    // Only a call whose function carries an ExecReach is screened — the exec a session built over
    // its own mounts — and only where the path lands on a mount with a shell. A call the person is
    // being asked about anyway never reaches here: asking is already what a flag would produce.
    private async Task<ExecScreenVerdict?> ScreenAsync(FunctionInvocationContext context, CancellationToken ct)
    {
        if (_execScreen is null
            || context.Function.GetService<ExecReach>() is not { } reach
            || ArgumentText(context.Arguments, "path") is not { } path
            || reach.Of(path) is not { } shellReach)
        {
            return null;
        }

        // The model the turn asked for, read as the preload reads it: a worker's request carries
        // no patch of its own, only its parent turn's context.
        var lastUser = context.Messages.LastOrDefault(m => m.Role == ChatRole.User);
        var turnModel = lastUser?.GetConfigPatch()?.Model ?? lastUser?.GetConversationContext()?.ConfigPatchModel;

        return await _execScreen.ScreenAsync(
            new ExecScreenRequest(
                shellReach, ArgumentText(context.Arguments, "command") ?? "", path, context.Messages, turnModel)
            {
                AgentId = _agentId,
                ConversationId = _conversationId
            },
            ct);
    }

    private static string? ArgumentText(AIFunctionArguments arguments, string name) =>
        arguments.TryGetValue(name, out var value)
            ? value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                null => null,
                _ => value.ToString()
            }
            : null;

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
            options?.Instructions, GetService(typeof(ServedRoute)) as ServedRoute));

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
                _observer.OnInvoked(Describe(result, Interlocked.Increment(ref _observed) - 1));
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

    private async ValueTask<object?> InvokeWithMetricsAsync(
        FunctionInvocationContext context,
        string toolName,
        CancellationToken cancellationToken)
    {
        // A tool call is measured whether it returned or threw, which is why this used to be the
        // same latency block twice. The scope publishes on both paths from one statement, and the
        // tool-call event reads its duration off the scope rather than a second stopwatch.
        using var latency = _metricsPublisher.MeasureLatency(LatencyStage.ToolExec, _conversationId);
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