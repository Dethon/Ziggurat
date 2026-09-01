using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

// A turn sent to the Lemonade chat host. The box speaks the OpenAI Responses wire the way
// OpenRouter does — tools, streamed function-call events, the tool-result round trip, usage and
// nested reasoning effort were all verified on a real box — so the turn rides the same pipeline
// (decoration, hydration, per-turn truncation, metrics) pointed at the box's address. What differs
// is done at the wire: the model id loses its namespace so the box sees what it advertised, the
// bearer token is sent only when a key is set, and any failure to get an answer — refused
// connection, timeout, non-2xx — becomes one named error, with no retry and no fallback. The
// OpenRouter-only body fields are left in place; the box tolerates them.
public sealed class LemonadeChatClient : IChatClient
{
    private readonly OpenRouterChatClient _pipeline;
    private readonly string _address;

    public LemonadeChatClient(
        LemonadeChatHostOptions host,
        IMetricsPublisher? metricsPublisher = null,
        string? sessionId = null,
        HttpMessageHandler? transportHandler = null,
        IAttachmentSource? attachmentSource = null,
        int hydrationDepthMessages = AttachmentHydration.DefaultDepthMessages,
        IReadImageStore? readImageStore = null,
        Func<string, bool>? modelAcceptsImages = null,
        Func<string, int?>? contextWindowFor = null)
    {
        _address = host.Address;
        var hasKey = !string.IsNullOrWhiteSpace(host.ApiKey);
        _pipeline = new OpenRouterChatClient(
            host.BaseAddress.ToString(),
            // The SDK refuses an empty credential; the header it produces from this placeholder
            // is removed on the wire when the box checks no key.
            hasKey ? host.ApiKey! : "no-key",
            // Never what a request carries: every turn routed here resolved to a Lemonade model,
            // and that model rides the turn's options.
            LemonadeModelId.Prefix.TrimEnd('/'),
            metricsPublisher: metricsPublisher,
            sessionId: sessionId,
            providerRouting: null,
            transportHandler: new WireHandler(host.Address, hasKey)
            {
                InnerHandler = transportHandler ?? HostedConnectionPool.Shared
            },
            attachmentSource: attachmentSource,
            hydrationDepthMessages: hydrationDepthMessages,
            readImageStore: readImageStore,
            modelAcceptsImages: modelAcceptsImages,
            contextWindowFor: contextWindowFor,
            // A timed-out turn is one the SDK would otherwise send again; the box must never see
            // a turn twice, so the wire's own failures are the only ones and each is thrown once.
            maxRetries: 0);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = await GetStreamingResponseAsync(messages, options, cancellationToken).ToListAsync(cancellationToken);
        return updates.ToChatResponse();
    }

    // What the wire throws that the handler did not already name — a timeout surfacing as a
    // cancellation, the SDK's wrapper round a transport error, a connection dropped mid-stream —
    // is the host's failure unless the caller itself gave up. A defect anywhere else in the
    // pipeline keeps its own name.
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var updates = _pipeline.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await updates.MoveNextAsync())
                {
                    yield break;
                }
            }
            catch (Exception ex) when (IsWireFailure(ex) && !cancellationToken.IsCancellationRequested)
            {
                throw LemonadeChatHostException.From(_address, ex);
            }

            // The adapter reads the box's response id as a server-side conversation to continue,
            // and the agent refuses a turn that offers one because the history is its own. The box
            // keeps nothing between turns; the id stays as the message id it already became.
            updates.Current.ConversationId = null;
            yield return updates.Current;
        }
    }

    private static bool IsWireFailure(Exception ex) =>
        ex is ClientResultException or HttpRequestException or IOException or OperationCanceledException;

    public object? GetService(Type serviceType, object? key = null) =>
        serviceType.IsInstanceOfType(this) ? this : _pipeline.GetService(serviceType, key);

    public void Dispose() => _pipeline.Dispose();

    // Where the box's id is written and where its failures are named. Thrown as this client's own
    // exception rather than returned as a failed response, so the SDK's retry policy — which
    // retries the status codes a busy box answers with — never sends the turn a second time.
    private sealed class WireHandler(string address, bool hasKey) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!hasKey)
            {
                request.Headers.Authorization = null;
            }

            await StripNamespaceAsync(request, cancellationToken);

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw LemonadeChatHostException.From(address, ex);
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new LemonadeChatHostException(
                address, $"it answered {(int)response.StatusCode} {response.ReasonPhrase}: {detail}".TrimEnd(' ', ':'));
        }

        private static async Task StripNamespaceAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post ||
                request.Content?.Headers.ContentType?.MediaType?
                    .Equals("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }

            var body = await request.Content.ReadAsStringAsync(ct);
            if (JsonNode.Parse(body) is not JsonObject obj || obj["model"]?.GetValue<string>() is not { } model)
            {
                return;
            }

            obj["model"] = LemonadeModelId.Bare(model);
            request.Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
        }
    }
}