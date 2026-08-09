using System.Text.Json;
using Domain.DTOs.Channel;
using Infrastructure.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

// Whether a model can take an attachment is discovered rather than configured, so nobody has to
// keep a hand-written list of which model reads what. The provider is asked; when the provider
// cannot be asked, the last answer that worked stands, and with no answer ever the feature stays
// switched on — a blip at the provider must not remove it from everyone.
public sealed class OpenRouterModelCapabilitiesTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _client = new();
    private readonly OpenRouterModelCapabilities _capabilities;

    public OpenRouterModelCapabilitiesTests()
    {
        _client.BaseAddress = new Uri($"{_server.Url}/api/v1/");
        _capabilities = new OpenRouterModelCapabilities(
            _client, NullLogger<OpenRouterModelCapabilities>.Instance);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task AcceptedKinds_AreReadFromTheProvidersModelList()
    {
        StubModels(200, Catalogue());

        await _capabilities.RefreshAsync(CancellationToken.None);

        _capabilities.GetAcceptedAttachmentKinds("openai/gpt-5.6-luna")
            .ShouldBe([AttachmentKind.Image, AttachmentKind.Document], ignoreOrder: true);
        _capabilities.GetAcceptedAttachmentKinds("z-ai/glm-5.2").ShouldBeEmpty();
        _capabilities.GetAcceptedAttachmentKinds("openai/sees-only").ShouldBe([AttachmentKind.Image]);
    }

    [Fact]
    public async Task AModelIdWrittenAsAnAlias_ResolvesToTheModelItNames()
    {
        StubModels(200, Catalogue());

        await _capabilities.RefreshAsync(CancellationToken.None);

        _capabilities.GetAcceptedAttachmentKinds("~z-ai/glm-5.2").ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefresh_ReplacesTheValuesWithoutARestart()
    {
        StubModels(200, Catalogue());
        await _capabilities.RefreshAsync(CancellationToken.None);
        _capabilities.GetAcceptedAttachmentKinds("z-ai/glm-5.2").ShouldBeEmpty();

        _server.Reset();
        StubModels(200, """
            { "data": [ { "id": "z-ai/glm-5.2", "architecture": { "input_modalities": ["text", "image"] } } ] }
            """);
        await _capabilities.RefreshAsync(CancellationToken.None);

        _capabilities.GetAcceptedAttachmentKinds("z-ai/glm-5.2").ShouldBe([AttachmentKind.Image]);
    }

    [Fact]
    public async Task AFailedRefresh_LeavesThePreviousValuesInPlace()
    {
        StubModels(200, Catalogue());
        await _capabilities.RefreshAsync(CancellationToken.None);

        _server.Reset();
        StubModels(503, "upstream is having a moment");
        await _capabilities.RefreshAsync(CancellationToken.None);

        _capabilities.GetAcceptedAttachmentKinds("z-ai/glm-5.2").ShouldBeEmpty();
        _capabilities.GetAcceptedAttachmentKinds("openai/gpt-5.6-luna")
            .ShouldBe([AttachmentKind.Image, AttachmentKind.Document], ignoreOrder: true);
    }

    [Fact]
    public async Task WithNothingCachedAndAFailedLookup_CapabilityIsPermissive()
    {
        StubModels(503, "upstream is having a moment");

        await _capabilities.RefreshAsync(CancellationToken.None);

        _capabilities.GetAcceptedAttachmentKinds("z-ai/glm-5.2")
            .ShouldBe(AttachmentKinds.All, ignoreOrder: true);
    }

    [Fact]
    public async Task AModelTheProviderNeverListed_IsPermissive()
    {
        StubModels(200, Catalogue());

        await _capabilities.RefreshAsync(CancellationToken.None);

        _capabilities.GetAcceptedAttachmentKinds("some/model-nobody-has-heard-of")
            .ShouldBe(AttachmentKinds.All, ignoreOrder: true);
    }

    private static string Catalogue() => JsonSerializer.Serialize(new
    {
        data = new object[]
        {
            new
            {
                id = "openai/gpt-5.6-luna",
                architecture = new { input_modalities = new[] { "text", "image", "file" } }
            },
            new
            {
                id = "z-ai/glm-5.2",
                architecture = new { input_modalities = new[] { "text" } }
            },
            new
            {
                id = "openai/sees-only",
                architecture = new { input_modalities = new[] { "text", "image" } }
            }
        }
    });

    private void StubModels(int statusCode, string body) =>
        _server.Given(Request.Create().WithPath("/api/v1/models").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));
}