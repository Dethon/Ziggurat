using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Infrastructure.Metrics;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Infrastructure.Agents.ChatClients;

public sealed class OpenRouterChatClient : IChatClient
{
    private readonly IChatClient _client;
    private readonly HttpClient? _httpClient;
    private readonly HttpClientPipelineTransport? _transport;
    private readonly ConcurrentQueue<string> _reasoningQueue = new();
    private readonly ConcurrentQueue<decimal> _costQueue = new();
    private readonly ConcurrentQueue<long> _cachedTokenQueue = new();
    private readonly IMetricsPublisher _metricsPublisher;
    private readonly int? _maxContextTokens;
    private readonly string _model;
    private readonly TimeProvider _timeProvider;
    private readonly IAttachmentSource? _attachmentSource;
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
        int hydrationDepthMessages = AttachmentHydration.DefaultDepthMessages)
    {
        _model = model;
        _maxContextTokens = maxContextTokens;
        _metricsPublisher = metricsPublisher ?? NoOpMetricsPublisher.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _attachmentSource = attachmentSource;
        _hydrationDepthMessages = hydrationDepthMessages;
        _httpClient = CreateHttpClient(
            _reasoningQueue, _costQueue, _cachedTokenQueue, sessionId, providerRouting, transportHandler);
        _transport = new HttpClientPipelineTransport(_httpClient);
        _client = CreateClient(endpoint, apiKey, model, _transport);
    }

    internal OpenRouterChatClient(
        IChatClient innerClient,
        string model,
        int? maxContextTokens = null,
        IMetricsPublisher? metricsPublisher = null,
        TimeProvider? timeProvider = null,
        IAttachmentSource? attachmentSource = null,
        int hydrationDepthMessages = AttachmentHydration.DefaultDepthMessages)
    {
        _model = model;
        _maxContextTokens = maxContextTokens;
        _metricsPublisher = metricsPublisher ?? NoOpMetricsPublisher.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _attachmentSource = attachmentSource;
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
        var transformedMessages = await AttachmentHydration.ApplyAsync(
            decorated, _attachmentSource, _hydrationDepthMessages, ct);

        // The model a per-message config patch resolved to rides this request's own options
        // (McpAgent.CreateRunOptions puts it there), so metrics stamp what the request ran on
        // and a concurrent turn has nothing shared to overwrite.
        var effectiveModel = options?.ModelId ?? _model;

        var sender = transformedMessages
            .LastOrDefault(m => m.Role == ChatRole.User)
            ?.GetSenderId();

        var fixedOverhead = MessageTruncator.EstimateOptionsOverheadTokens(options);
        var truncated = MessageTruncator.Truncate(
            transformedMessages, _maxContextTokens,
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
                MaxContextTokens = _maxContextTokens ?? 0
            });
        }

        UsageContent? usage = null;

        await foreach (var update in _client.GetStreamingResponseAsync(truncated, options, ct))
        {
            AppendReasoningContent(update);
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
        return serviceType == typeof(ChatClientMetadata)
            ? Metadata
            : serviceType.IsInstanceOfType(this)
                ? this
                : _client.GetService(serviceType, key);
    }

    public void Dispose()
    {
        _client.Dispose();
        _transport?.Dispose();
        _httpClient?.Dispose();
    }

    private void AppendReasoningContent(ChatResponseUpdate update)
    {
        var reasoning = DrainReasoningQueue();
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            update.Contents.Add(new TextReasoningContent(reasoning));
        }
    }

    private string DrainReasoningQueue()
    {
        if (_reasoningQueue.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        while (_reasoningQueue.TryDequeue(out var chunk))
        {
            sb.Append(chunk);
        }

        return sb.ToString();
    }

    private static IChatClient CreateClient(
        string endpoint, string apiKey, string model, HttpClientPipelineTransport transport)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            Transport = transport
        };

        return new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(model)
            .AsIChatClient();
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
        ConcurrentQueue<string> reasoningQueue, ConcurrentQueue<decimal> costQueue,
        ConcurrentQueue<long> cachedQueue, string? sessionId, ProviderRouting? providerRouting,
        HttpMessageHandler? transportHandler = null)
    {
        var handler = new ReasoningHandler(
            reasoningQueue, costQueue, cachedQueue, sessionId, providerRouting)
        {
            InnerHandler = transportHandler ?? HostedConnectionPool.Shared
        };
        return new HttpClient(handler, disposeHandler: false);
    }

    private sealed class ReasoningHandler(
        ConcurrentQueue<string> reasoningQueue, ConcurrentQueue<decimal> costQueue,
        ConcurrentQueue<long> cachedQueue, string? sessionId, ProviderRouting? providerRouting)
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
                response.Content = OpenRouterHttpHelpers.WrapWithReasoningTee(
                    response.Content, reasoningQueue, costQueue, cachedQueue);
            }

            return response;
        }
    }
}