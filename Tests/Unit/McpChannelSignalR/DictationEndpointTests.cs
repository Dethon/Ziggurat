using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Transcription;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Dictation;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// A recording goes in and words come back. Nothing is kept: no reference is minted, nothing lands
// in the upload store, and the sweeper and retention rules never see it.
public sealed class DictationEndpointTests : IAsyncLifetime
{
    private const string Space = "default";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTranscriber _transcriber = new();
    private readonly RecordingMetricsPublisher _metrics = new();

    private AttachmentSettings _attachmentSettings = null!;
    private DictationSettings _settings = null!;
    private AttachmentTickets _tickets = null!;
    private AttachmentStore _store = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _attachmentSettings = new AttachmentSettings { StoragePath = _root, TicketTtlSeconds = 60 };
        _settings = new DictationSettings { MaxLength = TimeSpan.FromSeconds(2) };
        _tickets = new AttachmentTickets(_attachmentSettings, _time);
        _store = new AttachmentStore(_attachmentSettings, _time, NullLogger<AttachmentStore>.Instance);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddSingleton(_settings)
            .AddSingleton(_tickets)
            .AddSingleton<IAudioTranscriber>(_transcriber)
            .AddSingleton<IMetricsPublisher>(_metrics)
            .AddLogging();

        _app = builder.Build();
        DictationEndpoints.Map(_app);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ARecordingPostedWithAValidTicket_ComesBackAsWords()
    {
        _transcriber.Result = new() { Text = "  pon el temporizador  " };
        var ticket = _tickets.MintDictation(Space);

        var response = await PostAsync(ticket.Token, Space, Wav(1024));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var transcript = await response.Content.ReadFromJsonAsync<DictationTranscript>();
        transcript!.Text.ShouldBe("pon el temporizador");
    }

    // A dictation produces composer text, so it must not force a conversation into existence the
    // way picking a file does, and it must not spend one of a message's attachment slots.
    [Fact]
    public async Task ARecording_LeavesNothingBehindInTheUploadStore()
    {
        _transcriber.Result = new() { Text = "hola" };
        var ticket = _tickets.MintDictation(Space);

        await PostAsync(ticket.Token, Space, Wav(1024));

        Directory.Exists(_root).ShouldBeFalse();
        _tickets.MintUpload("topic-1", "7:42", Space).Token.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ARecordingWithNoTicket_IsRefused()
    {
        var response = await PostAsync(ticket: null, Space, Wav(64));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _transcriber.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARecordingWithAnUnknownTicket_IsRefused()
    {
        var response = await PostAsync("not-a-ticket", Space, Wav(64));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARecordingWithAnExpiredTicket_IsRefused()
    {
        var ticket = _tickets.MintDictation(Space);
        _time.Advance(TimeSpan.FromSeconds(_attachmentSettings.TicketTtlSeconds + 1));

        var response = await PostAsync(ticket.Token, Space, Wav(64));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARecordingWithATicketMintedInAnotherSpace_IsRefused()
    {
        var ticket = _tickets.MintDictation("private");

        var response = await PostAsync(ticket.Token, Space, Wav(64));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The cap is the server's to enforce: a browser that ignores the limit it was handed, or one
    // that never asked, must not be able to post an hour of audio.
    [Fact]
    public async Task ARecordingLongerThanTheCap_IsRefusedWhateverTheClientClaims()
    {
        var ticket = _tickets.MintDictation(Space);

        var response = await PostAsync(ticket.Token, Space, Wav((int)_settings.MaxBytes + 1));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        _transcriber.Requests.ShouldBeEmpty();
    }

    // The part's own content type is the browser's word for it, and a claim is not a container.
    [Fact]
    public async Task ARecordingWhoseBytesAreNotAudioAtAll_IsRefusedWhateverThePartClaims()
    {
        var ticket = _tickets.MintDictation(Space);

        var response = await PostAsync(ticket.Token, Space, [1, 2, 3, 4, 5, 6, 7, 8]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        _transcriber.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARequestThatIsNotOneAudioFile_IsRefused()
    {
        var ticket = _tickets.MintDictation(Space);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{DictationEndpointPaths.Transcriptions}?space={Space}")
        {
            Content = new StringContent("not a form")
        };
        request.Headers.Add(DictationEndpointPaths.TicketHeader, ticket.Token);

        var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // The recording is already gone by the time this is known, so the only thing left to do is
    // say so plainly; the browser turns any non-2xx into its one-line composer refusal.
    [Fact]
    public async Task ATranscriberFailure_IsAnsweredRatherThanThrown()
    {
        _transcriber.Fails = new TimeoutException("Lemonade did not answer");
        var ticket = _tickets.MintDictation(Space);

        var response = await PostAsync(ticket.Token, Space, Wav(1024));

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task TheAudioReachesTheTranscriberWithTheTypeItWasPostedUnder()
    {
        _transcriber.Result = new() { Text = "hola" };
        var ticket = _tickets.MintDictation(Space);
        var audio = Wav(64);

        await PostAsync(ticket.Token, Space, audio);

        var request = _transcriber.Requests.ShouldHaveSingleItem();
        request.MediaType.ShouldBe("audio/wav");
        request.Audio.ToArray().ShouldBe(audio);
    }

    // The satellites' own speech-to-text members, from this call site too: an operator watching
    // whisper get slow after a model change sees the chat channel on the same dashboard.
    [Fact]
    public async Task ADictation_RecordsItsLatencyAndATranscribedUtteranceAgainstTheChatChannel()
    {
        _transcriber.Result = new() { Text = "hola", AvgLogProb = -0.2 };
        var ticket = _tickets.MintDictation(Space);

        await PostAsync(ticket.Token, Space, Wav(1024));

        var voice = _metrics.Published.OfType<VoiceEvent>().ToList();
        voice.ShouldContain(e => e.Metric == VoiceMetric.SttLatencyMs && e.Channel == "web");
        voice.ShouldContain(e => e.Metric == VoiceMetric.UtteranceTranscribed && e.Channel == "web");
    }

    [Fact]
    public async Task AFailedTranscription_RecordsTheErrorMember()
    {
        _transcriber.Fails = new TimeoutException("Lemonade did not answer");
        var ticket = _tickets.MintDictation(Space);

        await PostAsync(ticket.Token, Space, Wav(1024));

        _metrics.Published.OfType<VoiceEvent>()
            .ShouldContain(e => e.Metric == VoiceMetric.SttError && e.Channel == "web");
    }

    // What the browser actually posts: 16 kHz mono s16le WAV. The endpoint decides the container
    // from these bytes, so a test that posted zeroes would be posting nothing recognisable.
    private static byte[] Wav(int payloadBytes) =>
        WavAudio.FromPcm(
            new byte[payloadBytes], new AudioFormat { SampleRateHz = 16_000, Channels = 1, SampleWidthBytes = 2 });

    private async Task<HttpResponseMessage> PostAsync(string? ticket, string space, byte[] audio)
    {
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        body.Add(file, "file", "dictation.wav");

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{DictationEndpointPaths.Transcriptions}?space={space}")
        { Content = body };
        if (ticket is not null)
        {
            request.Headers.Add(DictationEndpointPaths.TicketHeader, ticket);
        }

        return await _client.SendAsync(request);
    }
}