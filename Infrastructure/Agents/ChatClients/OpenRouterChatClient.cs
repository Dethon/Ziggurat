using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Infrastructure.Agents.Mcp;
using Infrastructure.Metrics;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Infrastructure.Agents.ChatClients;

public sealed class OpenRouterChatClient : IChatClient
{
    private readonly IChatClient _client;
    private readonly HttpClient? _httpClient;
    private readonly HttpClientPipelineTransport? _transport;
    private readonly ConcurrentQueue<decimal> _costQueue = new();
    private readonly ConcurrentQueue<long> _cachedTokenQueue = new();
    private readonly ServedRouteSink _routeSink = new();
    private readonly IMetricsPublisher _metricsPublisher;
    private readonly Func<string, int?> _contextWindowFor;
    private readonly string _model;
    private readonly TimeProvider _timeProvider;
    private readonly IAttachmentSource? _attachmentSource;
    private readonly IReadImageStore? _readImageStore;
    private readonly Func<string, bool> _modelAcceptsImages;
    private readonly int _hydrationDepthMessages;

    public OpenRouterChatClient(
        string endpoint,
        string apiKey,
        string model,
        int? maxContextTokens = null,
        IMetricsPublisher? metricsPublisher = null,
        string? sessionId = null,
        TimeProvider? timeProvider = null,
        ProviderRouting? providerRouting = null,
        HttpMessageHandler? transportHandler = null,
        IAttachmentSource? attachmentSource = null,
        int hydrationDepthMessages = AttachmentHydration.DefaultDepthMessages,
        IReadImageStore? readImageStore = null,
        Func<string, bool>? modelAcceptsImages = null,
        Func<string, int?>? contextWindowFor = null,
        int? maxRetries = null)
    {
        _model = model;
        _contextWindowFor = contextWindowFor ?? (_ => maxContextTokens);
        _metricsPublisher = metricsPublisher ?? NoOpMetricsPublisher.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _attachmentSource = attachmentSource;
        _readImageStore = readImageStore is null ? null : new ForgetOnceReadImageStore(readImageStore);
        _modelAcceptsImages = modelAcceptsImages ?? (_ => true);
        _hydrationDepthMessages = hydrationDepthMessages;
        _httpClient = CreateHttpClient(
            _costQueue, _cachedTokenQueue, _routeSink, sessionId, providerRouting, transportHandler);
        _transport = new HttpClientPipelineTransport(_httpClient);
        _client = CreateClient(endpoint, apiKey, model, _transport, maxRetries);
    }

    internal OpenRouterChatClient(
        IChatClient innerClient,
        string model,
        int? maxContextTokens = null,
        IMetricsPublisher? metricsPublisher = null,
        TimeProvider? timeProvider = null,
        IAttachmentSource? attachmentSource = null,
        int hydrationDepthMessages = AttachmentHydration.DefaultDepthMessages,
        IReadImageStore? readImageStore = null,
        Func<string, bool>? modelAcceptsImages = null,
        Func<string, int?>? contextWindowFor = null)
    {
        _model = model;
        _contextWindowFor = contextWindowFor ?? (_ => maxContextTokens);
        _metricsPublisher = metricsPublisher ?? NoOpMetricsPublisher.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _attachmentSource = attachmentSource;
        _readImageStore = readImageStore is null ? null : new ForgetOnceReadImageStore(readImageStore);
        _modelAcceptsImages = modelAcceptsImages ?? (_ => true);
        _hydrationDepthMessages = hydrationDepthMessages;
        _client = innerClient;
    }

    private ChatClientMetadata Metadata => _client.GetService<ChatClientMetadata>() ?? new ChatClientMetadata();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var allUpdates = await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToListAsync(cancellationToken);
        return allUpdates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Decorated on the way out and never on the way in: what the client sends carries the
        // sender, the local time, the recall block and the bytes of any attachment, and what gets
        // persisted stays as typed.
        var decorated = messages
            .Select(x => TurnDecoration.Apply(x, _timeProvider.LocalTimeZone))
            .ToList();
        // The model a per-message config patch resolved to rides this request's own options
        // (McpAgent.CreateRunOptions puts it there), so metrics stamp what the request ran on
        // and a concurrent turn has nothing shared to overwrite. Resolved before hydration,
        // because whether an image part may travel at all depends on it: the wire rejects a
        // request carrying an image the model cannot take rather than stripping it.
        var effectiveModel = options?.ModelId ?? _model;

        // The conversation reaches this send on the turn's own options, the way MCP tool metadata
        // already does: a chat client is built per model from DI, so there is nothing per
        // conversation for it to have been constructed with.
        var transformedMessages = await AttachmentHydration.ApplyAsync(
            decorated,
            _attachmentSource,
            new ReadImageContext(
                _readImageStore,
                ConversationContextMeta.TryRead(options)?.ConversationId,
                _modelAcceptsImages(effectiveModel)),
            _hydrationDepthMessages,
            ct);

        var sender = transformedMessages
            .LastOrDefault(m => m.Role == ChatRole.User)
            ?.GetSenderId();

        // Decided for this turn rather than at construction, and from the model the turn runs on:
        // a model a person switched to for one turn may hold less than the agent's own does.
        var maxContextTokens = _contextWindowFor(effectiveModel);
        var fixedOverhead = MessageTruncator.EstimateOptionsOverheadTokens(options);
        var truncated = MessageTruncator.Truncate(
            transformedMessages, maxContextTokens,
            out var droppedCount, out var tokensBefore, out var tokensAfter,
            out var overflowDetected, fixedOverheadTokens: fixedOverhead);

        if (overflowDetected)
        {
            _metricsPublisher.Publish(new ContextTruncationEvent
            {
                Sender = sender ?? "unknown",
                Model = effectiveModel,
                DroppedMessages = droppedCount,
                EstimatedTokensBefore = tokensBefore,
                EstimatedTokensAfter = tokensAfter,
                MaxContextTokens = maxContextTokens ?? 0
            });
        }

        UsageContent? usage = null;

        await foreach (var update in _client.GetStreamingResponseAsync(truncated, options, ct))
        {
            // The Responses adapter leaves MessageId empty where the chat wire stamped the
            // completion id, and everything that reassembles a streamed turn keys on it. The
            // response id is one id per model turn — the aggregation the consumers want.
            update.MessageId ??= update.ResponseId;
            update.SetTimestamp(_timeProvider.GetUtcNow());

            var updateUsage = update.Contents.OfType<UsageContent>().FirstOrDefault();
            if (updateUsage is not null)
            {
                usage = updateUsage;
            }

            yield return update;
        }

        if (usage?.Details is not null)
        {
            var cost = DrainCostQueue() ?? 0m;
            _metricsPublisher.Publish(new TokenUsageEvent
            {
                Sender = sender ?? "unknown",
                Model = effectiveModel,
                InputTokens = (int)(usage.Details.InputTokenCount ?? 0),
                OutputTokens = (int)(usage.Details.OutputTokenCount ?? 0),
                CachedInputTokens = DrainCachedTokenQueue() ?? ReadCachedInputTokens(usage.Details),
                Cost = cost
            });
        }
    }

    // Matched on the key name rather than a fixed constant: the provider/SDK spelling of this
    // counter has moved between versions (prompt_tokens_details.cached_tokens ->
    // InputTokenDetails.CachedTokenCount), and guessing wrong silently reports "no caching" for a
    // model that is in fact caching most of its prompt.
    private static long? ReadCachedInputTokens(UsageDetails details)
    {
        if (details.AdditionalCounts is null)
        {
            return null;
        }

        foreach (var (key, value) in details.AdditionalCounts)
        {
            if (key.Contains("cach", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    public object? GetService(Type serviceType, object? key = null)
    {
        if (serviceType == typeof(ChatClientMetadata))
        {
            return Metadata;
        }

        // Asked for by an evaluation harness after a turn, and by nothing in a deployment. The
        // route only exists once a response has streamed, so a caller that asks before one has
        // gets null rather than the configured model dressed up as the served one.
        if (serviceType == typeof(ServedRoute))
        {
            return _routeSink.Current;
        }

        return serviceType.IsInstanceOfType(this)
            ? this
            : _client.GetService(serviceType, key);
    }

    public void Dispose()
    {
        _client.Dispose();
        _transport?.Dispose();
        _httpClient?.Dispose();
    }

    // The Responses wire, because it is the one on which a tool result may carry an image: a
    // Chat Completions tool message is a plain string, and that constraint shaped the whole
    // read-image feature until this client switched. OpenRouter serves it at the same base URL,
    // honours session_id, provider routing and usage accounting on it, and translates it for
    // non-OpenAI models. See docs/adr/0029.
    private static IChatClient CreateClient(
        string endpoint, string apiKey, string model, HttpClientPipelineTransport transport, int? maxRetries)
    {
        var options = new ResponsesClientOptions
        {
            Endpoint = new Uri(endpoint),
            Transport = transport
        };
        // Unset is the hosted default: the SDK's budget and backoff for transient failures, and a
        // longer, provider-hinted one for a 429 (OpenRouterRetryPolicy). Zero is for a host that
        // must never see a turn twice.
        options.RetryPolicy = new OpenRouterRetryPolicy(maxRetries ?? OpenRouterRetryPolicy.DefaultMaxRetries);

        return new ResponsesClient(new ApiKeyCredential(apiKey), options)
            .AsIChatClient(model);
    }

    // Mirrors DrainCostQueue: the provider reports this once per response, in the same usage block.
    internal long? DrainCachedTokenQueue()
    {
        long? last = null;
        while (_cachedTokenQueue.TryDequeue(out var cached))
        {
            last = cached;
        }
        return last;
    }

    internal decimal? DrainCostQueue()
    {
        decimal? last = null;
        while (_costQueue.TryDequeue(out var cost))
        {
            last = cost;
        }

        return last;
    }

    // One handler (= one connection pool) for the whole process: a per-conversation
    // handler would pay a fresh TCP+TLS handshake to OpenRouter on every new
    // conversation's first LLM call.
    internal static SocketsHttpHandler SharedHandler => HostedConnectionPool.Shared;

    private static HttpClient CreateHttpClient(
        ConcurrentQueue<decimal> costQueue,
        ConcurrentQueue<long> cachedQueue, ServedRouteSink routeSink,
        string? sessionId, ProviderRouting? providerRouting,
        HttpMessageHandler? transportHandler = null)
    {
        var handler = new WireHandler(costQueue, cachedQueue, routeSink, sessionId, providerRouting)
        {
            InnerHandler = transportHandler ?? HostedConnectionPool.Shared
        };
        return new HttpClient(handler, disposeHandler: false);
    }

    // Stamps what OpenRouter reads off the body (session pin, provider routing, usage accounting)
    // and taps what its typed response drops (cost, the prompt-cache counter) on the way back.
    private sealed class WireHandler(
        ConcurrentQueue<decimal> costQueue,
        ConcurrentQueue<long> cachedQueue, ServedRouteSink routeSink,
        string? sessionId, ProviderRouting? providerRouting)
        : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
                request, sessionId, providerRouting, cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);

            if (response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream",
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                response.Content = OpenRouterHttpHelpers.WrapWithUsageTee(
                    response.Content, costQueue, cachedQueue, routeSink);
            }

            return response;
        }
    }
}