using System.Net;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class OpenAiSpeechToTextTests
{
    // Captures the multipart form structurally (field name → string value, plus the file part)
    // instead of matching substrings against the serialized body — raw-substring assertions like
    // ShouldContain("es") match trivially inside header text ("charset").
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        public Dictionary<string, string> Fields { get; } = [];
        public string? FileName { get; private set; }
        public byte[]? FileBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri;
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var disposition = part.Headers.ContentDisposition!;
                    if (disposition.FileName is { } fileName)
                    {
                        FileName = fileName.Trim('"');
                        FileBytes = await part.ReadAsByteArrayAsync(ct);
                    }
                    else
                    {
                        Fields[disposition.Name!.Trim('"')] = await part.ReadAsStringAsync(ct);
                    }
                }
            }
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static OpenAiSpeechToText Sut(HttpMessageHandler handler, OpenAiSttConfig? config = null) =>
        new(
            new StubClientFactory(handler),
            config ?? new OpenAiSttConfig { Language = "es" },
            NullLogger<OpenAiSpeechToText>.Instance);

    private static async IAsyncEnumerable<AudioChunk> Chunks(params byte[][] payloads)
    {
        foreach (var payload in payloads)
        {
            yield return new AudioChunk
            {
                Data = payload,
                Format = AudioFormat.WyomingStandard,
                Timestamp = TimeSpan.Zero
            };
            await Task.Yield();
        }
    }

    [Fact]
    public async Task TranscribeAsync_VerboseJson_ParsesTextAndDurationWeightedSignals()
    {
        // Weighted by segment duration: avg_logprob (1*-0.2 + 3*-0.8)/4 = -0.65,
        // no_speech_prob (1*0.1 + 3*0.3)/4 = 0.25.
        var sut = Sut(new StubHandler(_ => Json("""
        {
          "task": "transcribe", "language": "es", "duration": 4.0, "text": "hola mundo",
          "segments": [
            { "id": 0, "start": 0.0, "end": 1.0, "text": "hola", "avg_logprob": -0.2, "no_speech_prob": 0.1 },
            { "id": 1, "start": 1.0, "end": 4.0, "text": "mundo", "avg_logprob": -0.8, "no_speech_prob": 0.3 }
          ]
        }
        """)));

        var result = await sut.TranscribeAsync(
            Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None);

        result.Text.ShouldBe("hola mundo");
        result.Language.ShouldBe("es");
        result.AvgLogProb!.Value.ShouldBe(-0.65, 1e-9);
        result.NoSpeechProb!.Value.ShouldBe(0.25, 1e-9);
        result.Confidence.ShouldBeNull();
        result.CompressionRatio.ShouldBeNull();
    }

    [Fact]
    public async Task TranscribeAsync_PlainJsonBody_FailsOpenWithNullSignals()
    {
        var sut = Sut(new StubHandler(_ => Json("""{ "text": "hola" }""")));

        var result = await sut.TranscribeAsync(
            Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None);

        result.Text.ShouldBe("hola");
        result.Language.ShouldBeNull();
        result.AvgLogProb.ShouldBeNull();
        result.NoSpeechProb.ShouldBeNull();
    }

    [Fact]
    public async Task TranscribeAsync_SendsWavMultipartWithModelFormatAndLanguage()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler);
        var audio = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        await sut.TranscribeAsync(
            Chunks(audio[..16], audio[16..]), new TranscriptionOptions(), CancellationToken.None);

        handler.LastUri!.ToString().ShouldBe("http://lemonade:13305/v1/audio/transcriptions");
        handler.FileName.ShouldBe("utterance.wav");
        handler.Fields["model"].ShouldBe("Whisper-Large-v3-Turbo");   // config default
        handler.Fields["response_format"].ShouldBe("verbose_json");
        handler.Fields["language"].ShouldBe("es");

        var wav = handler.FileBytes!;
        System.Text.Encoding.ASCII.GetString(wav[..4]).ShouldBe("RIFF");
        System.Text.Encoding.ASCII.GetString(wav[8..12]).ShouldBe("WAVE");
        BitConverter.ToInt16(wav, 22).ShouldBe((short)1);      // mono
        BitConverter.ToInt32(wav, 24).ShouldBe(16000);         // incoming satellite rate
        BitConverter.ToInt16(wav, 34).ShouldBe((short)16);     // 16-bit
        BitConverter.ToInt32(wav, 40).ShouldBe(32);            // data length
        wav[44..76].ShouldBe(audio);                           // both chunks concatenated
    }

    [Fact]
    public async Task TranscribeAsync_OptionsLanguage_OverridesConfigLanguage()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hello" }"""));
        var sut = Sut(handler);

        await sut.TranscribeAsync(
            Chunks(new byte[32]), new TranscriptionOptions { Language = "en" }, CancellationToken.None);

        handler.Fields["language"].ShouldBe("en");
    }

    [Fact]
    public async Task TranscribeAsync_EmptyAudio_ReturnsEmptyWithoutHttpCall()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "ghost" }"""));
        var sut = Sut(handler);

        var result = await sut.TranscribeAsync(Chunks(), new TranscriptionOptions(), CancellationToken.None);

        result.Text.ShouldBe("");
        handler.Calls.ShouldBe(0);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    // A hung Lemonade must surface as a TimeoutException so the host's SttError path fires;
    // plain OperationCanceledException is swallowed there as connection teardown.
    [Fact]
    public async Task TranscribeAsync_LemonadeHangs_ThrowsTimeoutException()
    {
        var sut = Sut(
            new HangingHandler(),
            new OpenAiSttConfig { RequestTimeout = TimeSpan.FromMilliseconds(50) });

        // WaitAsync guards the suite: without the feature this call would hang forever.
        await Should.ThrowAsync<TimeoutException>(() =>
                sut.TranscribeAsync(Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TranscribeAsync_CallerCancels_ThrowsCancellationNotTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var sut = Sut(
            new HangingHandler(),
            new OpenAiSttConfig { RequestTimeout = TimeSpan.FromSeconds(30) });

        await Should.ThrowAsync<TaskCanceledException>(() =>
            sut.TranscribeAsync(Chunks(new byte[32]), new TranscriptionOptions(), cts.Token));
    }

    [Fact]
    public async Task TranscribeAsync_Non2xx_Throws()
    {
        var sut = Sut(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        }));

        await Should.ThrowAsync<HttpRequestException>(() =>
            sut.TranscribeAsync(Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_BodyWithoutText_Throws()
    {
        var sut = Sut(new StubHandler(_ => Json("""{ "status": "ok" }""")));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync(Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_NoConfiguredPrompt_OmitsThePromptField()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler);

        await sut.TranscribeAsync(Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None);

        handler.Fields.ShouldNotContainKey("prompt");
    }

    [Fact]
    public async Task TranscribeAsync_ConfiguredPrompt_SendsItWithPlaceholdersResolved()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler, new OpenAiSttConfig { Prompt = "Órdenes en {room} ({locality})." });

        await sut.TranscribeAsync(
            Chunks(new byte[32]),
            new TranscriptionOptions { Room = "la cocina", Locality = "Valladolid" },
            CancellationToken.None);

        handler.Fields["prompt"].ShouldBe("Órdenes en la cocina (Valladolid).");
    }

    [Fact]
    public async Task TranscribeAsync_PriorText_IsAppendedAfterTheConfiguredText()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler, new OpenAiSttConfig { Prompt = "Órdenes breves." });

        await sut.TranscribeAsync(
            Chunks(new byte[32]),
            new TranscriptionOptions { PriorText = "pon el temporizador" },
            CancellationToken.None);

        handler.Fields["prompt"].ShouldBe("Órdenes breves. pon el temporizador");
    }

    [Fact]
    public async Task TranscribeAsync_OptionsPromptTemplate_OverridesConfigPrompt()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler, new OpenAiSttConfig { Prompt = "Global." });

        await sut.TranscribeAsync(
            Chunks(new byte[32]),
            new TranscriptionOptions { PromptTemplate = "Solo de la cocina." },
            CancellationToken.None);

        handler.Fields["prompt"].ShouldBe("Solo de la cocina.");
    }
}