using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Domain.Metrics;
using Domain.Prompts;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

public sealed class McpAgent : DisposableAgent
{
    private readonly string? _customInstructions;
    private readonly string? _language;
    private readonly string _description;
    private readonly IReadOnlyList<AIFunction> _domainTools;
    private readonly IReadOnlyList<string> _domainPrompts;
    private readonly string[] _endpoints;
    private readonly IReadOnlySet<string> _filesystemEnabledTools;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<McpAgent>? _logger;
    private readonly ChatClientAgent _innerAgent;
    private readonly string _name;
    private readonly string _userId;
    private readonly ReasoningEffort? _reasoningEffort;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly IMetricsPublisher _metricsPublisher;
    private readonly string _model;
    private readonly IReadOnlyList<string> _patchableModelIds;
    private readonly string _conversationId;
    private readonly McpPromptCache? _promptCache;

    private readonly ConcurrentDictionary<AgentSession, ThreadSession> _threadSessions = [];
    private int _isDisposed;

    public override string? Name => _innerAgent.Name;
    public override string? Description => _innerAgent.Description;

    // Both halves of a per-message config patch, resolved once for a turn. The values ride
    // that turn's ChatOptions, so nothing per-request lives on the shared chat client and the
    // model stamped on metrics is by construction the model the request ran on.
    private sealed record TurnConfig(string? ModelOverride, ReasoningEffort? Effort);

    // The spec carries every configured value, so nothing about what this agent is can be
    // expressed by omitting an argument. What is left are the live collaborators, and the
    // metrics publisher is one of them and required: handing the agent no publisher is what
    // silently cost every subagent its turn latency.
    public McpAgent(
        AgentSpec spec,
        IChatClient chatClient,
        IThreadStateStore stateStore,
        IMetricsPublisher metricsPublisher,
        TimeProvider timeProvider,
        IReadOnlyList<AIFunction> domainTools,
        IReadOnlyList<string> domainPrompts,
        ILoggerFactory? loggerFactory = null,
        McpPromptCache? promptCache = null)
    {
        _endpoints = spec.McpServerEndpoints;
        _filesystemEnabledTools = spec.FilesystemEnabledTools;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<McpAgent>();
        _name = spec.DisplayName;
        _description = spec.Description;
        _userId = spec.UserId;
        _customInstructions = spec.CustomInstructions;
        _language = spec.Language;
        _domainTools = domainTools;
        _domainPrompts = domainPrompts;
        _reasoningEffort = ParseEffort(spec.ReasoningEffort);
        _timeProvider = timeProvider;
        _metricsPublisher = metricsPublisher;
        _model = spec.Model;
        _patchableModelIds = spec.PatchableModelIds;
        _conversationId = spec.ConversationId;
        _promptCache = promptCache;
        _innerAgent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = spec.DisplayName,
            Description = spec.Description,
            ChatHistoryProvider = new RedisChatMessageStore(
                stateStore, metricsPublisher, spec.ConversationId)
        });
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        await _syncLock.WithLockAsync(async () =>
        {
            foreach (var session in _threadSessions.Values)
            {
                await session.DisposeAsync();
            }

            _threadSessions.Clear();
        });
        _syncLock.Dispose();
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        return _innerAgent.CreateSessionAsync(cancellationToken);
    }

    public override async Task WarmupSessionAsync(AgentSession thread, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        // Pre-create the ThreadSession (MCP connections + tool discovery) so this
        // cost overlaps with first-message handling. The _syncLock in
        // GetOrCreateSessionAsync makes the subsequent RunStreaming reuse it.
        using var latency = _metricsPublisher.MeasureLatency(LatencyStage.SessionWarmup, _conversationId);
        await GetOrCreateSessionAsync(thread, ct);
    }

    // Only what the session already built. Nothing is created here: a caller asking before the
    // session exists is asking about an agent that has not warmed up, and the answer is "no
    // filesystem yet" rather than a session built as a side effect of the question.
    public override IVirtualFileSystemRegistry? GetFileSystemRegistry(AgentSession thread) =>
        _threadSessions.TryGetValue(thread, out var session) ? session.FileSystemRegistry : null;

    public override async ValueTask DisposeThreadSessionAsync(AgentSession thread)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        await _syncLock.WithLockAsync(async () =>
        {
            if (_threadSessions.Remove(thread, out var session))
            {
                await session.DisposeAsync();
            }
        });
    }

    protected override async ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        return await _innerAgent.SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedThread,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        // ChatClientAgentSession expects { "stateBag": { "ChatHistoryProviderState": "..." }, "conversationId": ... }
        if (serializedThread.TryGetProperty("stateBag", StringComparison.InvariantCultureIgnoreCase, out _))
        {
            return _innerAgent.DeserializeSessionAsync(serializedThread, jsonSerializerOptions, cancellationToken);
        }

        // Legacy format: plain AgentKey string or other value — wrap into stateBag
        var json = new JsonObject
        {
            ["stateBag"] = new JsonObject
            {
                [RedisChatMessageStore.StateKey] = serializedThread.ToJsonNode()
            }
        };
        serializedThread = JsonSerializer.Deserialize<JsonElement>(json.ToJsonString());
        return _innerAgent.DeserializeSessionAsync(serializedThread, jsonSerializerOptions, cancellationToken);
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        var response = RunCoreStreamingAsync(messages, thread, options, cancellationToken);
        return (await response.ToArrayAsync(cancellationToken)).ToAgentResponse();
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var turnConfig = ResolveTurnConfig(messageList, options);
        return WithLlmLatencyAsync(
            RunCoreStreamingInnerAsync(messageList, thread, options, turnConfig, cancellationToken),
            turnConfig.ModelOverride ?? _model,
            cancellationToken);
    }

    private async IAsyncEnumerable<AgentResponseUpdate> WithLlmLatencyAsync(
        IAsyncEnumerable<AgentResponseUpdate> source,
        string? effectiveModel,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var total = _metricsPublisher.MeasureLatency(
            LatencyStage.LlmTotal, _conversationId, model: effectiveModel);
        // The first token is a point inside the total span, not a span of its own, so its scope is
        // disposed on the first update rather than by a using. A stream that yields nothing never
        // disposes it and publishes no first-token latency, which is what the old firstEmitted flag
        // did too — there is no such thing as the first token of an empty stream.
        var firstToken = _metricsPublisher.MeasureLatency(
            LatencyStage.LlmFirstToken, _conversationId, model: effectiveModel);
        await foreach (var update in source.WithCancellation(ct))
        {
            firstToken.Dispose();
            yield return update;
        }
    }

    private async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingInnerAsync(
        IReadOnlyList<ChatMessage> messageList,
        AgentSession? thread,
        AgentRunOptions? options,
        TurnConfig turnConfig,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        thread ??= await CreateSessionAsync(cancellationToken);
        var session = await GetOrCreateSessionAsync(thread, cancellationToken);

        var conversationContext = messageList
            .Select(m => m.GetConversationContext())
            .FirstOrDefault(c => c is not null);

        options ??= CreateRunOptions(session, conversationContext, turnConfig);

        await foreach (var update in _innerAgent.RunStreamingAsync(messageList, thread, options, cancellationToken))
        {
            yield return update;
        }
    }

    // One resolution site for the whole patch, and one rejection rule for both fields: fall
    // back to the configured value and warn. A bad override never costs the user a turn, but
    // it never disappears silently either.
    private TurnConfig ResolveTurnConfig(IReadOnlyList<ChatMessage> messages, AgentRunOptions? suppliedOptions)
    {
        // A caller-supplied option set replaces everything CreateRunOptions would have built:
        // instructions, tools, reasoning effort and the config patch. Non-channel callers
        // (harnesses, benchmarks) legitimately do this, so it stays possible — but an agent
        // running stripped of what makes it that agent must not look like a normal turn.
        if (suppliedOptions is not null)
        {
            _logger?.LogWarning(
                "Agent '{AgentName}' ran with caller-supplied AgentRunOptions; this turn uses none of " +
                "its instructions, tools, reasoning effort or config patch",
                _name);
            return new TurnConfig(
                (suppliedOptions as ChatClientAgentRunOptions)?.ChatOptions?.ModelId, null);
        }

        var patch = messages
            .LastOrDefault(m => m.Role == ChatRole.User)
            ?.GetConfigPatch();

        return new TurnConfig(ResolveModelOverride(patch?.Model), ResolveEffort(patch?.ReasoningEffort));
    }

    private string? ResolveModelOverride(string? patchedModel)
    {
        if (string.IsNullOrWhiteSpace(patchedModel) ||
            string.Equals(patchedModel, _model, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Return the whitelist's own casing, not the patch's: OpenRouter model IDs are lowercase
        // slugs, and stamping the patch's casing verbatim can turn a valid override into a
        // model-not-found error.
        var resolved = _patchableModelIds
            .FirstOrDefault(id => string.Equals(id, patchedModel, StringComparison.OrdinalIgnoreCase));

        if (resolved is null)
        {
            LogRejectedPatch("model", patchedModel, _model);
        }

        return resolved;
    }

    private ReasoningEffort? ResolveEffort(string? patchedEffort)
    {
        if (string.IsNullOrWhiteSpace(patchedEffort))
        {
            return _reasoningEffort;
        }

        var parsed = TryParseEffort(patchedEffort);
        if (parsed is null)
        {
            LogRejectedPatch("reasoningEffort", patchedEffort, _reasoningEffort?.ToString());
        }

        return parsed ?? _reasoningEffort;
    }

    // A client whose whitelist has drifted from the agent's shows up here: the rejected value
    // in the log is the evidence.
    private void LogRejectedPatch(string field, string value, string? fallback)
    {
        _logger?.LogWarning(
            "Rejected config patch {Field}={Value}; using {Fallback}", field, value, fallback ?? "unset");
    }

    private ChatClientAgentRunOptions CreateRunOptions(
        ThreadSession session, ConversationContext? conversationContext, TurnConfig turnConfig)
    {
        return new ChatClientAgentRunOptions(new ChatOptions
        {
            ModelId = turnConfig.ModelOverride,
            Tools = [.. session.Tools],
            Instructions = BuildInstructions(
                _name,
                _description,
                _customInstructions,
                _language,
                _domainPrompts,
                session.FileSystemPrompts,
                session.ClientManager.Prompts,
                _timeProvider.GetLocalNow()),
            Reasoning = turnConfig.Effort is null
                ? null
                : new ReasoningOptions { Effort = turnConfig.Effort.Value },
            AdditionalProperties = BuildConversationContextProperties(conversationContext)
        });
    }

    // The 2026-07-28 protocol dropped MCP sessions: a server may no longer treat connection or
    // process identity as a proxy for conversation continuity, so every tools/call has to carry
    // its own context. The property bag is therefore built unconditionally -- an absent context
    // is a defect, not a mode.
    //
    // It is reported rather than thrown, deliberately. The context is metadata that only some
    // downstream servers consume (Library search scoping, WebSearch browser sessions); the LLM
    // turn itself does not need it. Throwing would trade a degraded tool call for a dead
    // user-facing turn, and it would do so on the whole of McpAgent, whose non-channel callers
    // (harnesses, benchmarks, any future non-channel trigger) legitimately run without one.
    // What this task exists to prevent is the SILENT omission, and an error log fixes that.
    private AdditionalPropertiesDictionary BuildConversationContextProperties(ConversationContext? conversationContext)
    {
        if (conversationContext is null)
        {
            _logger?.LogError(
                "Agent '{AgentName}' ran without a ConversationContext; MCP tool calls in this run carry no '{MetaKey}' _meta",
                _name, ChannelProtocol.ConversationContextMetaKey);
            return [];
        }

        return new AdditionalPropertiesDictionary { [ConversationContextMeta.OptionsKey] = conversationContext };
    }

    internal static string BuildInstructions(
        string name,
        string? description,
        string? customInstructions,
        string? language,
        IEnumerable<string> domainPrompts,
        IEnumerable<string> fileSystemPrompts,
        IEnumerable<string> clientPrompts,
        DateTimeOffset now)
    {
        var prompts = domainPrompts
            .Concat(fileSystemPrompts)
            .Concat(clientPrompts);

        // Identity sits right after the core directive and before feature guidance, so the
        // model knows which agent it is before reading any feature- or tool-specific prompt.
        if (!string.IsNullOrWhiteSpace(name))
        {
            prompts = prompts.Prepend(IdentityPrompt.Build(name, description));
        }

        prompts = prompts.Prepend(BasePrompt.Instructions);

        // The date goes after every static section, never first. It is the only part of the
        // instructions that changes on its own, and the provider's prompt cache keys on a byte
        // prefix -- dating the opening line threw away the whole cached prefix at every midnight
        // to say one sentence. Behind the static sections, a rollover only re-prefills the tail.
        prompts = prompts.Append(
            $"Today is {now.ToString("dddd, yyyy-MM-dd", CultureInfo.InvariantCulture)}.");

        // User custom instructions go LAST: closest to the conversation, they are the
        // most recent (and least "lost in the middle") guidance the model sees, which
        // matters for action-time rules like "acknowledge before calling a tool".
        if (!string.IsNullOrEmpty(customInstructions))
        {
            prompts = prompts.Append(customInstructions);
        }

        // The reply language outranks even the custom instructions: it is a hard output
        // constraint, and every other section -- plus the tool results the model reads right
        // before answering -- is English, so it goes last, closest to the conversation.
        if (LanguagePrompt.Build(language) is { } languagePrompt)
        {
            prompts = prompts.Append(languagePrompt);
        }

        return string.Join("\n\n", prompts);
    }

    internal static ReasoningEffort? ParseEffort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.ToLowerInvariant() switch
        {
            "none" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "xhigh" or "extrahigh" or "extra-high" or "max" => ReasoningEffort.ExtraHigh,
            _ => throw new ArgumentException(
                $"Unknown reasoningEffort '{value}'. Valid values: none, low, medium, high, xhigh.",
                nameof(value))
        };
    }

    internal static ReasoningEffort? TryParseEffort(string? value)
    {
        try
        {
            return ParseEffort(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<ThreadSession> GetOrCreateSessionAsync(AgentSession thread, CancellationToken ct)
    {
        return await _syncLock.WithLockAsync(async () =>
        {
            if (_threadSessions.TryGetValue(thread, out var existing))
            {
                return existing;
            }

            var newSession = await ThreadSession
                .CreateAsync(_endpoints, _name, _userId, _description,
                             _domainTools, _filesystemEnabledTools, _loggerFactory,
                             ct, _promptCache);
            _threadSessions[thread] = newSession;
            return newSession;
        }, ct);
    }
}