using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// The upload store from the outside: a ticket is what gets a file in, a reference is what comes
// back, and the bytes come out again unchanged. Every refusal is a refusal a person could have
// predicted from the settings.
public sealed class AttachmentEndpointTests : IAsyncLifetime
{
    private const string TopicId = "topic-1";
    private const string ConversationId = "7:42";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));

    private AttachmentSettings _settings = null!;
    private AttachmentTickets _tickets = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _settings = new AttachmentSettings
        {
            StoragePath = _root,
            MaxBytesPerFile = 1024,
            MaxFilesPerMessage = 2,
            TicketTtlSeconds = 60
        };

        _tickets = new AttachmentTickets(_settings, _time);
        var store = new AttachmentStore(_settings, new RetentionSettings(), _time, NullLogger<AttachmentStore>.Instance);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddSingleton(_settings)
            .AddSingleton(_tickets)
            .AddSingleton(store);

        _app = builder.Build();
        AttachmentEndpoints.Map(_app);
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
    public async Task AFileUploadedWithAValidTicket_IsAnsweredWithAReference()
    {
        var ticket = _tickets.MintUpload(TopicId, ConversationId, "default");

        var response = await UploadAsync(ticket.Token, TopicId, "hello"u8.ToArray(), "photo.png", "image/png");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reference = await response.Content.ReadFromJsonAsync<AttachmentReference>();
        reference.ShouldNotBeNull();
        reference.FileName.ShouldBe("photo.png");
        reference.MediaType.ShouldBe("image/png");
        reference.SizeBytes.ShouldBe(5);
        reference.Id.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task StoredBytes_ReadBackThroughTheDownloadEndpoint_Unchanged()
    {
        var payload = Encoding.UTF8.GetBytes("the original bytes, exactly");
        var ticket = _tickets.MintUpload(TopicId, ConversationId, "default");
        var reference = await UploadAndReadReferenceAsync(ticket.Token, payload, "scan.pdf", "application/pdf");

        var download = _tickets.MintDownload(reference.Id);
        var response = await _client.GetAsync($"/api/attachments/{reference.Id}?ticket={download.Token}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(payload);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/pdf");
    }

    [Fact]
    public async Task AnUploadWithNoTicket_IsRefused()
    {
        var response = await UploadAsync(ticket: null, TopicId, "x"u8.ToArray(), "photo.png", "image/png");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnUploadWithAnExpiredTicket_IsRefused()
    {
        var ticket = _tickets.MintUpload(TopicId, ConversationId, "default");
        _time.Advance(TimeSpan.FromSeconds(_settings.TicketTtlSeconds + 1));

        var response = await UploadAsync(ticket.Token, TopicId, "x"u8.ToArray(), "photo.png", "image/png");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnUploadWithATicketForAnotherTopic_IsRefused()
    {
        var ticket = _tickets.MintUpload("some-other-topic", "9:9", "default");

        var response = await UploadAsync(ticket.Token, TopicId, "x"u8.ToArray(), "photo.png", "image/png");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AFileAboveTheMaximumSize_IsRefusedWithAReadableReason()
    {
        var ticket = _tickets.MintUpload(TopicId, ConversationId, "default");

        var response = await UploadAsync(
            ticket.Token, TopicId, new byte[_settings.MaxBytesPerFile + 1], "photo.png", "image/png");

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadAsStringAsync()).ShouldContain("photo.png");
    }

    [Fact]
    public async Task AMediaTypeOutsideImagesAndPdf_IsRefusedWithAReadableReason()
    {
        var ticket = _tickets.MintUpload(TopicId, ConversationId, "default");

        var response = await UploadAsync(
            ticket.Token, TopicId, "x"u8.ToArray(), "notes.txt", "text/plain");

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        // The same wording the composer would have used at pick time, quoting the same file.
        (await response.Content.ReadAsStringAsync())
            .ShouldBe(AttachmentRefusals.For("notes.txt", "text/plain", 1, _settings.Limits));
    }

    [Fact]
    public async Task MoreFilesThanThePerMessageMaximum_AreRefused()
    {
        var ticket = _tickets.MintUpload(TopicId, ConversationId, "default");

        foreach (var index in Enumerable.Range(0, _settings.MaxFilesPerMessage))
        {
            var accepted = await UploadAsync(
                ticket.Token, TopicId, "x"u8.ToArray(), $"photo-{index}.png", "image/png");
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var refused = await UploadAsync(
            ticket.Token, TopicId, "x"u8.ToArray(), "one-too-many.png", "image/png");

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ADownloadWithNoTicket_IsRefused()
    {
        var ticket = _tickets.MintUpload(TopicId, ConversationId, "default");
        var reference = await UploadAndReadReferenceAsync(
            ticket.Token, "x"u8.ToArray(), "photo.png", "image/png");

        var response = await _client.GetAsync($"/api/attachments/{reference.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ADownloadOfAFileThatIsGone_Answers404()
    {
        var download = _tickets.MintDownload("7-42/deadbeef");

        var response = await _client.GetAsync($"/api/attachments/7-42/deadbeef?ticket={download.Token}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<AttachmentReference> UploadAndReadReferenceAsync(
        string ticket, byte[] payload, string fileName, string mediaType)
    {
        var response = await UploadAsync(ticket, TopicId, payload, fileName, mediaType);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AttachmentReference>())!;
    }

    private async Task<HttpResponseMessage> UploadAsync(
        string? ticket, string topicId, byte[] payload, string fileName, string mediaType)
    {
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(payload);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        body.Add(file, "file", fileName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/attachments?topicId={topicId}")
        { Content = body };
        if (ticket is not null)
        {
            request.Headers.Add(AttachmentEndpointPaths.TicketHeader, ticket);
        }

        return await _client.SendAsync(request);
    }
}