using System.Net;
using System.Text;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Transcription;

public class LemonadeTranscriptionClientTests
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
        public string? FileMediaType { get; private set; }
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
                        FileMediaType = part.Headers.ContentType?.MediaType;
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

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LemonadeTranscriptionClient Sut(
        HttpMessageHandler handler, TranscriptionClientConfig? config = null) =>
        new(
            new StubClientFactory(handler),
            config ?? new TranscriptionClientConfig { Language = "es" },
            NullLogger.Instance);

    private static TranscriptionRequest Wav(params byte[] audio) =>
        new() { Audio = audio, MediaType = "audio/wav" };

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

        var result = await sut.TranscribeAsync(Wav(new byte[32]), CancellationToken.None);

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

        var result = await sut.TranscribeAsync(Wav(new byte[32]), CancellationToken.None);

        result.Text.ShouldBe("hola");
        result.AvgLogProb.ShouldBeNull();
        result.NoSpeechProb.ShouldBeNull();
    }

    [Fact]
    public async Task TranscribeAsync_PostsTheAudioAsItArrivedWithModelFormatAndLanguage()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler);
        var audio = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        await sut.TranscribeAsync(Wav(audio), CancellationToken.None);

        handler.LastUri!.ToString().ShouldBe("http://lemonade:13305/v1/audio/transcriptions");
        handler.FileMediaType.ShouldBe("audio/wav");
        handler.FileBytes.ShouldBe(audio);
        handler.Fields["model"].ShouldBe("Whisper-Large-v3-Turbo");   // config default
        handler.Fields["response_format"].ShouldBe("verbose_json");
        handler.Fields["language"].ShouldBe("es");
    }

    // whisper-server decodes MP3, FLAC and Ogg/Vorbis itself, so a channel forwards those bytes
    // untouched; the part still needs a filename whose extension matches what it carries.
    [Theory]
    [InlineData("audio/wav", "dictation.wav")]
    [InlineData("audio/mpeg", "dictation.mp3")]
    [InlineData("audio/flac", "dictation.flac")]
    [InlineData("audio/ogg", "dictation.ogg")]
    public async Task TranscribeAsync_NamesTheFilePartAfterTheMediaType(string mediaType, string expected)
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler);

        await sut.TranscribeAsync(
            new TranscriptionRequest { Audio = new byte[32], MediaType = mediaType }, CancellationToken.None);

        handler.FileName.ShouldBe(expected);
        handler.FileMediaType.ShouldBe(mediaType);
    }

    [Fact]
    public async Task TranscribeAsync_RequestOverrides_BeatTheConfiguredDefaults()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hello" }"""));
        var sut = Sut(handler, new TranscriptionClientConfig { Language = "es", Prompt = "Global." });

        await sut.TranscribeAsync(
            new TranscriptionRequest
            {
                Audio = new byte[32],
                MediaType = "audio/wav",
                FileName = "utterance.wav",
                Language = "en",
                Model = "Whisper-Large-v3-Turbo",
                Prompt = "Dictado largo."
            },
            CancellationToken.None);

        handler.FileName.ShouldBe("utterance.wav");
        handler.Fields["language"].ShouldBe("en");
        handler.Fields["model"].ShouldBe("Whisper-Large-v3-Turbo");
        handler.Fields["prompt"].ShouldBe("Dictado largo.");
    }

    [Fact]
    public async Task TranscribeAsync_NoPromptAnywhere_OmitsThePromptField()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler);

        await sut.TranscribeAsync(Wav(new byte[32]), CancellationToken.None);

        handler.Fields.ShouldNotContainKey("prompt");
    }

    [Fact]
    public async Task TranscribeAsync_NoLanguageAnywhere_OmitsTheLanguageField()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler, new TranscriptionClientConfig());

        await sut.TranscribeAsync(Wav(new byte[32]), CancellationToken.None);

        handler.Fields.ShouldNotContainKey("language");
    }

    [Fact]
    public async Task TranscribeAsync_EmptyAudio_ReturnsEmptyWithoutHttpCall()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "ghost" }"""));
        var sut = Sut(handler);

        var result = await sut.TranscribeAsync(Wav(), CancellationToken.None);

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

    // A hung Lemonade must surface as a TimeoutException, distinct from both a caller's
    // cancellation and from Lemonade answering with an error: only one of the three is worth
    // telling the person to record again about.
    [Fact]
    public async Task TranscribeAsync_LemonadeHangs_ThrowsTimeoutException()
    {
        var sut = Sut(
            new HangingHandler(),
            new TranscriptionClientConfig { RequestTimeout = TimeSpan.FromMilliseconds(50) });

        await Should.ThrowAsync<TimeoutException>(() =>
                sut.TranscribeAsync(Wav(new byte[32]), CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TranscribeAsync_RequestTimeout_OverridesTheConfiguredOne()
    {
        var sut = Sut(new HangingHandler(), new TranscriptionClientConfig { RequestTimeout = TimeSpan.FromMinutes(5) });

        await Should.ThrowAsync<TimeoutException>(() =>
                sut.TranscribeAsync(
                    new TranscriptionRequest
                    {
                        Audio = new byte[32],
                        MediaType = "audio/wav",
                        Timeout = TimeSpan.FromMilliseconds(50)
                    },
                    CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TranscribeAsync_CallerCancels_ThrowsCancellationNotTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var sut = Sut(new HangingHandler(), new TranscriptionClientConfig { RequestTimeout = TimeSpan.FromSeconds(30) });

        await Should.ThrowAsync<TaskCanceledException>(() =>
            sut.TranscribeAsync(Wav(new byte[32]), cts.Token));
    }

    [Fact]
    public async Task TranscribeAsync_Non2xx_Throws()
    {
        var sut = Sut(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Invalid request")
        }));

        var error = await Should.ThrowAsync<HttpRequestException>(() =>
            sut.TranscribeAsync(Wav(new byte[32]), CancellationToken.None));
        error.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TranscribeAsync_BodyWithoutText_Throws()
    {
        var sut = Sut(new StubHandler(_ => Json("""{ "status": "ok" }""")));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync(Wav(new byte[32]), CancellationToken.None));
    }
}