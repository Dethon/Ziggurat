using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Transcription;

// The one place that talks to Lemonade's OpenAI-compatible /audio/transcriptions route: the
// multipart shape, the model/language/prompt fields, the confidence figures read back out, and the
// timeout and error mapping. Both chat channels inject it as IAudioTranscriber and the voice hub's
// streaming speech-to-text posts through it too, so a whisper quirk is fixed once.
//
// response_format=verbose_json is always asked for, so the per-segment avg_logprob /
// no_speech_prob quality signals reach the callers that gate on them. The signals are
// duration-weighted across the body's segments (one POST usually carries one, but whisper may
// split); a body without segments (plain json shape) degrades to null signals and every gate on
// them fails open. Lemonade emits neither score nor compression_ratio — left null.
public sealed class LemonadeTranscriptionClient(
    IHttpClientFactory httpFactory,
    TranscriptionClientConfig config,
    ILogger logger) : IAudioTranscriber
{
    // Named registration for the Lemonade endpoints. Clients are created per call (never cached in
    // the singleton services) so IHttpClientFactory handler rotation keeps working.
    public const string ClientName = "lemonade";

    private static readonly Dictionary<string, string> ExtensionsByMediaType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/wave"] = ".wav",
        ["audio/mpeg"] = ".mp3",
        ["audio/mp3"] = ".mp3",
        ["audio/flac"] = ".flac",
        ["audio/x-flac"] = ".flac",
        ["audio/ogg"] = ".ogg",
        ["application/ogg"] = ".ogg"
    };

    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Audio.Length == 0)
        {
            return new TranscriptionResult { Text = "" };
        }

        using var content = BuildForm(request);
        using var response = await PostWithTimeoutAsync(content, request.Timeout ?? config.RequestTimeout, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        if (JsonNode.Parse(body) is not JsonObject json || json["text"] is null)
        {
            throw new InvalidOperationException("Malformed transcription response from Lemonade");
        }

        var result = ParseResult(json);
        logger.LogInformation(
            "Lemonade transcript: text={Text} lang={Lang} avg_logprob={AvgLogProb} no_speech_prob={NoSpeechProb}",
            result.Text, result.Language, result.AvgLogProb, result.NoSpeechProb);
        return result;
    }

    private MultipartFormDataContent BuildForm(TranscriptionRequest request)
    {
        var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(request.Audio.ToArray());
        audio.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        content.Add(audio, "file", request.FileName ?? FileNameFor(request.MediaType));
        content.Add(new StringContent(request.Model ?? config.Model), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        if ((request.Language ?? config.Language) is { } language)
        {
            content.Add(new StringContent(language), "language");
        }
        if ((request.Prompt ?? config.Prompt) is { } prompt)
        {
            content.Add(new StringContent(prompt), "prompt");
        }
        return content;
    }

    // whisper-server picks its decoder from the bytes, but it still refuses a part it cannot name,
    // so the extension has to match what the part carries.
    private static string FileNameFor(string mediaType) =>
        "dictation" + (ExtensionsByMediaType.GetValueOrDefault(mediaType) ?? ".bin");

    // PostAsync buffers the full response, so this covers body receipt too. The timeout surfaces as
    // TimeoutException, not OperationCanceledException: the satellite host swallows OCE as
    // connection teardown, and a hung Lemonade must reach its error path instead.
    private async Task<HttpResponseMessage> PostWithTimeoutAsync(
        MultipartFormDataContent content, TimeSpan requestTimeout, CancellationToken ct)
    {
        using var http = httpFactory.CreateClient(ClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(requestTimeout);
        try
        {
            return await http.PostAsync(
                $"{config.BaseUrl.TrimEnd('/')}/audio/transcriptions", content, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Lemonade transcription did not respond within {requestTimeout.TotalSeconds:F0}s");
        }
    }

    private static TranscriptionResult ParseResult(JsonObject json)
    {
        var weighted = ((json["segments"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(s => (
                Weight: Math.Max((ReadDouble(s, "end") ?? 0) - (ReadDouble(s, "start") ?? 0), 1e-9),
                Segment: s))
            .ToList();

        return new TranscriptionResult
        {
            Text = json["text"]?.GetValue<string>() ?? string.Empty,
            Language = json["language"]?.GetValue<string>(),
            AvgLogProb = WeightedMean(weighted, s => ReadDouble(s, "avg_logprob")),
            NoSpeechProb = WeightedMean(weighted, s => ReadDouble(s, "no_speech_prob"))
        };
    }

    // Segments differ in length, so a plain mean would let a short noise segment outvote long
    // clean speech. Weight by duration; segments without the value abstain (fail-open).
    private static double? WeightedMean(
        IReadOnlyList<(double Weight, JsonObject Segment)> weighted,
        Func<JsonObject, double?> selector)
    {
        var pairs = weighted
            .Where(w => selector(w.Segment) is not null)
            .Select(w => (w.Weight, Value: selector(w.Segment)!.Value))
            .ToList();
        return pairs.Count > 0
            ? pairs.Sum(p => p.Weight * p.Value) / pairs.Sum(p => p.Weight)
            : null;
    }

    // The quality signals are optional: absent, malformed or non-finite means "no signal" (null),
    // never an error. GetValue<double>() THROWS on a string or an object, and this body comes from
    // a peer, so read it tolerantly.
    private static double? ReadDouble(JsonObject json, string key)
    {
        if (json[key] is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            return d;
        }
        return value.TryGetValue<long>(out var l) ? l : null;
    }
}