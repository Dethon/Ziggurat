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
using Domain.Exceptions;
using Domain.Extensions;
using Domain.Skills;
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
    private readonly TimeZoneInfo? _turnZone;
    private readonly IAttachmentSource? _attachmentSource;
    private readonly IReadImageStore? _readImageStore;
    private readonly Func<string, bool> _modelAcceptsImages;
    private readonly Func<string, bool> _modelAcceptsVideo;
    private readonly int _maxPicturesPerRequest;
    private readonly bool _asksAgainAfterEmptyReply;

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
        int maxPicturesPerRequest = AttachmentHydration.DefaultMaxPictures,
        IReadImageStore? readImageStore = null,
        Func<string, bool>? modelAcceptsImages = null,
        Func<string, int?>? contextWindowFor = null,
        int? maxRetries = null,
        Func<string, bool>? modelAcceptsVideo = null,
        TimeZoneInfo? turnZone = null)
    {
        _model = model;
        _contextWindowFor = contextWindowFor ?? (_ => maxContextTokens);
        _metricsPublisher = metricsPublisher ?? NoOpMetricsPublisher.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _turnZone = turnZone;
        _attachmentSource = attachmentSource;
        _readImageStore = readImageStore is null ? null : new ForgetOnceReadImageStore(readImageStore);
        _modelAcceptsImages = modelAcceptsImages ?? (_ => true);
        _modelAcceptsVideo = modelAcceptsVideo ?? (_ => true);
        _maxPicturesPerRequest = maxPicturesPerRequest;
        _asksAgainAfterEmptyReply = maxRetries != 0;
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
        int maxPicturesPerRequest = AttachmentHydration.DefaultMaxPictures,
        IReadImageStore? readImageStore = null,
        Func<string, bool>? modelAcceptsImages = null,
        Func<string, int?>? contextWindowFor = null,
        Func<string, bool>? modelAcceptsVideo = null,
        TimeZoneInfo? turnZone = null,
        int? maxRetries = null)
    {
        _model = model;
        _contextWindowFor = contextWindowFor ?? (_ => maxContextTokens);
        _metricsPublisher = metricsPublisher ?? NoOpMetricsPublisher.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _turnZone = turnZone;
        _attachmentSource = attachmentSource;
        _readImageStore = readImageStore is null ? null : new ForgetOnceReadImageStore(readImageStore);
        _modelAcceptsImages = modelAcceptsImages ?? (_ => true);
        _modelAcceptsVideo = modelAcceptsVideo ?? (_ => true);
        _maxPicturesPerRequest = maxPicturesPerRequest;
        _asksAgainAfterEmptyReply = maxRetries != 0;
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
            .Select(x => TurnDecoration.Apply(x, _turnZone ?? _timeProvider.LocalTimeZone))
            .ToList();
        // The model a per-message config patch resolved to rides this request's own options
        // (McpAgent.CreateRunOptions puts it there), so metrics stamp what the request ran on
        // and a concurrent turn has nothing shared to overwrite. Resolved before hydration,
        // because whether an image part may travel at all depends on it: the wire rejects a
        // request carrying an image the model cannot take rather than stripping it. Whether a clip
        // or its stills go is the same question asked about video.
        var effectiveModel = options?.ModelId ?? _model;
        var inputs = new ModelInputs(
            effectiveModel, _modelAcceptsImages(effectiveModel), _modelAcceptsVideo(effectiveModel));

        // The conversation reaches this send on the turn's own options, the way MCP tool metadata
        // already does: a chat client is built per model from DI, so there is nothing per
        // conversation for it to have been constructed with.
        var turnContext = ConversationContextMeta.TryRead(options);
        var transformedMessages = await AttachmentHydration.ApplyAsync(
            decorated,
            _attachmentSource,
            turnContext,
            new ReadImageContext(_readImageStore, turnContext?.ConversationId, inputs.Images),
            _maxPicturesPerRequest,
            ct,
            inputs);

        var asking = transformedMessages.LastOrDefault(m => m.Role == ChatRole.User);
        var sender = asking?.GetSenderId();
        var account = asking?.GetAccountId();
        var agent = asking?.GetAgentId();
        var turn = new TurnLabels(asking?.GetConversationContext()?.Origin.ChannelId, asking?.GetAppTurn()?.AppId);

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
                UserId = account,
                Model = effectiveModel,
                DroppedMessages = droppedCount,
                EstimatedTokensBefore = tokensBefore,
                EstimatedTokensAfter = tokensAfter,
                MaxContextTokens = maxContextTokens ?? 0
            });
        }

        // Last, on what is left to send: a preload is recorded as a pair and sent as a note, and
        // everything above — who is speaking, what is pinned, what a group weighs — is asked of
        // the record, where the last user-role message is still the person's.
        var sent = VideoInputParts.AsSent(SkillPreloadNote.AsSent(truncated));

        // A provider now and then ends a reply with nothing in it — on prod, Pearl Research generated
        // what was by every sign a tool call and OpenRouter passed on none of it, finish "stop". After
        // a tool result that ends the turn with no word, so it is asked once more; nothing was shown,
        // so the second reply simply follows. Only there: after the person's own message an empty
        // reply is what the model chose.
        var asksAgain = _asksAgainAfterEmptyReply && sent.LastOrDefault()?.Role == ChatRole.Tool;
        for (var ask = 0; ; ask++)
        {
            UsageContent? usage = null;
            ChatFinishReason? finish = null;
            var answered = false;

            await foreach (var update in DsmlToolCalls.RepairAsync(
                               SchemaRefusalsAsync(_client.GetStreamingResponseAsync(sent, WithVerbatimStrictSchema(options), ct), options, ct), ct))
            {
                // The Responses adapter leaves MessageId empty where the chat wire stamped the
                // completion id, and everything that reassembles a streamed turn keys on it. The
                // response id is one id per model turn — the aggregation the consumers want.
                update.MessageId ??= update.ResponseId;
                update.SetTimestamp(_timeProvider.GetUtcNow());

                usage = update.Contents.OfType<UsageContent>().FirstOrDefault() ?? usage;
                finish = update.FinishReason ?? finish;
                answered |= Answers(update);

                yield return update;
            }

            // Each ask was paid for, so each is billed.
            PublishUsage(usage, sender, account, agent, effectiveModel, turn);

            if (answered || !asksAgain || ask > 0 || (finish is not null && finish != ChatFinishReason.Stop))
            {
                yield break;
            }
        }
    }

    // Words or a call; reasoning alone, or blank text, leaves the person with nothing to read.
    private static bool Answers(ChatResponseUpdate update) =>
        update.Contents.Any(c => c is FunctionCallContent || c is TextContent { Text: var text } && !string.IsNullOrWhiteSpace(text));

    // The channel a turn came on and the built app whose ask it was, which its usage is counted under.
    private sealed record TurnLabels(string? Channel, string? AppId);

    private void PublishUsage(
        UsageContent? usage, string? sender, string? account, string? agent, string model, TurnLabels turn)
    {
        if (usage?.Details is null)
        {
            return;
        }

        _metricsPublisher.Publish(new TokenUsageEvent
        {
            Sender = sender ?? "unknown",
            UserId = account,
            AgentId = agent,
            Channel = turn.Channel,
            AppId = turn.AppId,
            Model = model,
            InputTokens = (int)(usage.Details.InputTokenCount ?? 0),
            OutputTokens = (int)(usage.Details.OutputTokenCount ?? 0),
            CachedInputTokens = DrainCachedTokenQueue() ?? ReadCachedInputTokens(usage.Details),
            Cost = DrainCostQueue() ?? 0m
        });
    }

    // A strict schema goes out as it was written. The adapter would otherwise rewrite it into the
    // subset it assumes strict mode takes — moving `minimum`, `maximum` and their kind into the
    // description, where nothing enforces them — and the schema a channel asked for is a contract
    // it parses the answer against, not a hint. Pre-setting the text format through the raw
    // options is the adapter's own way in; it only fills a format nobody set. A non-strict
    // schema keeps the adapter's handling, which is what memory's lenient readers were built on.
    private static ChatOptions? WithVerbatimStrictSchema(ChatOptions? options)
    {
        if (!AsksStrictFormat(options) || options!.ResponseFormat is not ChatResponseFormatJson { Schema: { } schema } json)
        {
            return options;
        }

        var verbatim = options.Clone();
        var inner = options.RawRepresentationFactory;
        verbatim.RawRepresentationFactory = client =>
        {
            var raw = inner?.Invoke(client) as CreateResponseOptions ?? new CreateResponseOptions();
            raw.TextOptions ??= new ResponseTextOptions();
            raw.TextOptions.TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                json.SchemaName ?? "json_schema",
                BinaryData.FromString(schema.GetRawText()),
                json.SchemaDescription,
                jsonSchemaIsStrict: true);
            return raw;
        };
        return verbatim;
    }

    // A request carrying a strict schema that the provider answers 400 is refused for the schema more often than
    // not, and the same request fails alike every time: said as that refusal, so a channel whose caller wrote the
    // schema (a built app's ask) tells it to fix the schema rather than to ask again. The provider's words ride
    // along. Any other status, or a 400 to a request with no schema, is the failure it was.
    private static async IAsyncEnumerable<ChatResponseUpdate> SchemaRefusalsAsync(
        IAsyncEnumerable<ChatResponseUpdate> updates, ChatOptions? options, [EnumeratorCancellation] CancellationToken ct)
    {
        var strict = AsksStrictFormat(options);
        await using var reading = updates.GetAsyncEnumerator(ct);
        while (true)
        {
            bool more;
            try
            {
                more = await reading.MoveNextAsync();
            }
            catch (ClientResultException ex) when (strict && ex.Status == 400)
            {
                throw new ResponseFormatRefusedException(ex.Message, ex);
            }

            if (!more)
            {
                yield break;
            }

            yield return reading.Current;
        }
    }

    private static bool AsksStrictFormat(ChatOptions? options) =>
        options is { ResponseFormat: ChatResponseFormatJson { Schema: not null } }
        && options.AdditionalProperties?.TryGetValue(StrictResponseFormatKey, out var strict) == true
        && strict is true;

    // Set by McpAgent on a turn whose message asked for a format. Deliberately not the adapter's
    // own `strict` key: that one is a single switch for the format and every tool, and a strict
    // tool has its parameter schema rewritten — an optional parameter made required. Only the
    // answer's schema is asked to be strict; the tools go out as they would on any other turn.
    internal const string StrictResponseFormatKey = "ziggurat.strictResponseFormat";

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