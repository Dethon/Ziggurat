using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Infrastructure.Agents.ChatClients;

internal static class OpenRouterHttpHelpers
{
    public static async Task PrepareRequestBodyAsync(
        HttpRequestMessage request, string? sessionId, ProviderRouting? providerRouting,
        CancellationToken ct)
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

        // Pin the conversation to one provider so its prompt cache stays warm. Without this,
        // OpenRouter derives the sticky-routing key from a message hash, which churns every turn
        // because the opening bytes (timestamp prefix, memory context, "Today is ...") change.
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            obj["session_id"] = sessionId;
        }

        // Ask for the usage breakdown. `cost` arrives without this, but prompt_tokens_details —
        // which carries cached_tokens — does not, and that counter is the only direct measure of
        // whether the ~17k static prefix is actually being served from the provider's prompt cache.
        obj["usage"] = new JsonObject { ["include"] = true };

        // Per-agent provider routing. Omitted entirely when unset: OpenRouter's balanced load
        // balancing has no explicit `sort` value and is only reachable by sending no `sort` and
        // no `order` at all.
        //
        // Routing is a deployment constraint and applies to override turns too — every
        // `patchableModels` entry must be servable under the configured routing, or the
        // config is wrong.
        if (BuildProviderNode(providerRouting) is { } provider)
        {
            obj["provider"] = provider;
        }

        request.Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
    }

    internal static JsonObject? BuildProviderNode(ProviderRouting? routing)
    {
        if (routing is null || routing.IsEmpty)
        {
            return null;
        }

        var node = new JsonObject();

        if (routing.Sort is { } sort)
        {
            node["sort"] = sort.ToString().ToLowerInvariant();
        }

        AddSlugs(node, "order", routing.Order);
        AddSlugs(node, "only", routing.Only);
        AddSlugs(node, "ignore", routing.Ignore);

        if (routing.AllowFallbacks is { } allowFallbacks)
        {
            node["allow_fallbacks"] = allowFallbacks;
        }

        AddThreshold(node, "preferred_min_throughput", routing.PreferredMinThroughput);
        AddThreshold(node, "preferred_max_latency", routing.PreferredMaxLatency);
        AddMaxPrice(node, routing.MaxPrice);

        return node;
    }

    private static void AddSlugs(JsonObject node, string key, string[]? slugs)
    {
        if (slugs is not { Length: > 0 })
        {
            return;
        }

        node[key] = new JsonArray(slugs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    }

    private static void AddThreshold(JsonObject node, string key, ProviderThreshold? threshold)
    {
        if (threshold is null || threshold.IsEmpty)
        {
            return;
        }

        // A bare number is OpenRouter's documented shorthand for the p50 cutoff, so a p50-only
        // threshold goes out in the shape its own examples use rather than a one-key object.
        if (threshold is { P50: { } p50, P75: null, P90: null, P99: null })
        {
            node[key] = p50;
            return;
        }

        var cutoffs = new JsonObject();

        AddCutoff(cutoffs, "p50", threshold.P50);
        AddCutoff(cutoffs, "p75", threshold.P75);
        AddCutoff(cutoffs, "p90", threshold.P90);
        AddCutoff(cutoffs, "p99", threshold.P99);

        node[key] = cutoffs;
    }

    private static void AddMaxPrice(JsonObject node, ProviderMaxPrice? maxPrice)
    {
        if (maxPrice is null || maxPrice.IsEmpty)
        {
            return;
        }

        var ceilings = new JsonObject();

        AddCutoff(ceilings, "prompt", maxPrice.Prompt);
        AddCutoff(ceilings, "completion", maxPrice.Completion);
        AddCutoff(ceilings, "request", maxPrice.Request);
        AddCutoff(ceilings, "image", maxPrice.Image);

        node["max_price"] = ceilings;
    }

    private static void AddCutoff(JsonObject node, string key, double? cutoff)
    {
        if (cutoff is { } value)
        {
            node[key] = value;
        }
    }

    public static HttpContent WrapWithUsageTee(
        HttpContent inner, ConcurrentQueue<decimal> costQueue, ConcurrentQueue<long> cachedQueue)
    {
        return new TeeHttpContent(inner, costQueue, cachedQueue);
    }

    // Where this provider puts the usage block: at the root of a Chat Completions chunk, and under
    // `response` in the Responses wire's `response.completed` event. One resolver, so the two
    // extractors below cannot disagree about which wire they are reading.
    private static bool TryGetUsage(JsonElement root, out JsonElement usage)
    {
        if (root.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (root.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("usage", out usage) &&
            usage.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        usage = default;
        return false;
    }

    internal static long? ExtractCachedTokensFromSseData(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!TryGetUsage(doc.RootElement, out var usage))
            {
                return null;
            }

            // The counter moved names with the wire: prompt_tokens_details on Chat Completions,
            // input_tokens_details on Responses. Either way it is the only direct measure of
            // whether the static prefix is served from the provider's prompt cache.
            foreach (var name in (string[])["prompt_tokens_details", "input_tokens_details"])
            {
                if (usage.TryGetProperty(name, out var details) &&
                    details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("cached_tokens", out var cached) &&
                    cached.ValueKind == JsonValueKind.Number)
                {
                    return cached.GetInt64();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static decimal? ExtractCostFromSseData(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!TryGetUsage(doc.RootElement, out var usage))
            {
                return null;
            }

            if (!usage.TryGetProperty("cost", out var cost) ||
                cost.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            return cost.GetDecimal();
        }
        catch { return null; }
    }

    private sealed class TeeHttpContent(
        HttpContent inner, ConcurrentQueue<decimal> costQueue,
        ConcurrentQueue<long> cachedQueue) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var innerStream = await inner.ReadAsStreamAsync();
            await innerStream.CopyToAsync(stream);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            return new UsageTeeStream(await inner.ReadAsStreamAsync(), costQueue, cachedQueue);
        }

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

    // Reasoning needs no tap on this wire: the Responses adapter surfaces reasoning deltas as
    // TextReasoningContent itself. Cost and the cache counter are OpenRouter extensions the typed
    // usage drops, so they are still read off the stream on the way past.
    private sealed class UsageTeeStream(
        Stream inner, ConcurrentQueue<decimal> costQueue,
        ConcurrentQueue<long> cachedQueue) : Stream
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _buffer = new();

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                ProcessBytes(buffer.AsSpan(offset, read));
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                ProcessBytes(buffer.Span[..read]);
            }

            return read;
        }

        private void ProcessBytes(ReadOnlySpan<byte> bytes)
        {
            try
            {
                Span<char> chars = stackalloc char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                _decoder.Convert(bytes, chars, flush: false, out _, out var charsUsed, out _);
                _buffer.Append(chars[..charsUsed]);

                var text = _buffer.ToString();
                if (!text.Contains('\n'))
                {
                    return;
                }

                var lines = text.Split('\n');
                _buffer.Clear().Append(lines[^1]);
                var dataPayloads = lines
                    .Take(lines.Length - 1)
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => l.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    .Select(l => l[5..].Trim())
                    .Where(d => d.Length > 0 && d != "[DONE]")
                    .ToArray();

                foreach (var cost in dataPayloads
                    .Select(ExtractCostFromSseData)
                    .Where(c => c is not null))
                {
                    costQueue.Enqueue(cost!.Value);
                }

                foreach (var cached in dataPayloads
                    .Select(ExtractCachedTokensFromSseData)
                    .Where(c => c is not null))
                {
                    cachedQueue.Enqueue(cached!.Value);
                }
            }
            catch
            {
                // best-effort
            }
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
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
}