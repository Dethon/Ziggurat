using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Metrics;
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
    private int _observed;

    public ToolApprovalChatClient(
        IChatClient innerClient,
        IToolApprovalHandler approvalHandler,
        string conversationId,
        IEnumerable<string>? whitelistPatterns = null,
        IMetricsPublisher? metricsPublisher = null,
        IToolInvocationObserver? observer = null)
        : base(innerClient)
    {
        _observer = observer;
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

        if (_patternMatcher.IsMatch(toolName) || _dynamicallyApproved.Contains(toolName))
        {
            // The notification is display-only; overlapping it with the invocation keeps a
            // channel round trip off the tool's critical path. A notify failure still
            // surfaces, but no longer prevents the tool from executing.
            var notifyTask = _approvalHandler.NotifyAutoApprovedAsync(
                _conversationId, [request], cancellationToken);
            var invokeTask = InvokeWithMetricsAsync(context, toolName, cancellationToken).AsTask();
            await Task.WhenAll(notifyTask, invokeTask);
            return await invokeTask;
        }

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