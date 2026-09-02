using System.ClientModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
// is done at the wire: the model id loses its namespace so the box sees what it advertised,
// reasoning from earlier turns is not replayed because the box refuses it, the bearer token is
// sent only when a key is set, and any failure to get an answer — refused connection, timeout,
// non-2xx, or the error event the box streams inside a 200 — becomes one named error, with no
// retry and no fallback. The OpenRouter-only body fields are left in place; the box tolerates them.
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

            await RewriteForTheHostAsync(request, cancellationToken);

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw LemonadeChatHostException.From(address, ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                throw new LemonadeChatHostException(
                    address, $"it answered {(int)response.StatusCode} {response.ReasonPhrase}: {detail}".TrimEnd(' ', ':'));
            }

            // A request the box's backend refuses comes back as 200 and a stream whose only event
            // is the error, which the adapter reads as an empty reply. The first data event is
            // looked at before the adapter gets the stream.
            if (response.Content.Headers.ContentType?.MediaType?
                    .Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            {
                response.Content = new ErrorEventGuardContent(response.Content, address);
            }

            return response;
        }

        // The model id as the box advertised it, and no `reasoning` input items: the adapter
        // replays reasoning text that carries its item id as a reasoning item, and the box's
        // backend refuses one ("item['content'] is not an array") — with content, empty, too.
        private static async Task RewriteForTheHostAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post ||
                request.Content?.Headers.ContentType?.MediaType?
                    .Equals("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }

            var body = await request.Content.ReadAsStringAsync(ct);
            if (JsonNode.Parse(body) is not JsonObject obj)
            {
                return;
            }

            if (obj["model"]?.GetValue<string>() is { } model)
            {
                obj["model"] = LemonadeModelId.Bare(model);
            }

            if (obj["input"] is JsonArray input)
            {
                var kept = input
                    .Where(item => item?["type"]?.GetValue<string>() != "reasoning")
                    .Select(item => item?.DeepClone())
                    .ToArray();
                obj["input"] = new JsonArray(kept);
            }

            request.Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
        }
    }

    private sealed class ErrorEventGuardContent(HttpContent inner, string address) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var guarded = await CreateContentReadStreamAsync();
            await guarded.CopyToAsync(stream);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new ErrorEventGuardStream(await inner.ReadAsStreamAsync(), address);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // Reads ahead to the first `data:` event, throws the named error if that event is the box's
    // error object, and otherwise hands every byte on untouched. Keep-alive comments the box
    // sends while a model loads are not data events and are read past.
    private sealed class ErrorEventGuardStream(Stream inner, string address) : Stream
    {
        private const int InspectCap = 64 * 1024;
        private byte[] _held = [];
        private int _heldOffset;
        private bool _inspected;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_inspected)
            {
                await InspectAsync(cancellationToken);
            }

            if (_heldOffset < _held.Length)
            {
                var count = Math.Min(buffer.Length, _held.Length - _heldOffset);
                _held.AsSpan(_heldOffset, count).CopyTo(buffer.Span);
                _heldOffset += count;
                return count;
            }

            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        private async Task InspectAsync(CancellationToken ct)
        {
            _inspected = true;
            using var collected = new MemoryStream();
            var chunk = new byte[4096];
            while (collected.Length < InspectCap)
            {
                var read = await inner.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    break;
                }

                collected.Write(chunk, 0, read);
                if (FirstDataPayload(collected) is { } payload)
                {
                    ThrowIfError(payload);
                    break;
                }
            }

            _held = collected.ToArray();
        }

        private static string? FirstDataPayload(MemoryStream collected)
        {
            var text = Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length);
            return text.Split('\n')
                .SkipLast(1)
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                .Select(l => l[5..].Trim())
                .FirstOrDefault();
        }

        private void ThrowIfError(string payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("error", out var error)
                    || error.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                var code = error.TryGetProperty("code", out var c) ? c.ToString() : null;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : payload;
                throw new LemonadeChatHostException(address, $"it answered {code}: {message}".Replace("answered :", "answered"));
            }
            catch (JsonException)
            {
                // Not JSON, so not the box's error object; the adapter decides what it is.
            }
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}