using System.Text.Json;
using Domain.DTOs.Channel;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Clients;
using Microsoft.Extensions.Logging;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

// Which chat models the Lemonade chat host has is discovered, never declared: the host is asked
// and its answer is what is offered. A host that cannot be asked offers nothing — an absent box
// must not keep a list of models nothing can answer with.
public sealed class LemonadeModelDiscoveryTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly CapturingLoggerProvider _warnings = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        _clients.ForEach(c => c.Dispose());
        _server.Dispose();
    }

    [Fact]
    public async Task OnlyDownloadedChatModelsWithToolCalling_AreOffered()
    {
        StubModels(200, Models(
            Model("Qwen3.8-27B-GGUF-UD-Q4_K_XL", ["chat", "vision", "tool-calling"], downloaded: true),
            Model("Jan-nano-128k-GGUF", ["chat"], downloaded: true),
            Model("Devstral-Small-2507-GGUF", ["chat", "coding", "tool-calling"], downloaded: false),
            Model("Qwen3-Embedding-0.6B-GGUF", ["embeddings", "tool-calling"], downloaded: true)));
        var discovery = Discovery(_server.Url!);

        await discovery.RefreshAsync(CancellationToken.None);

        discovery.Current.Select(m => m.Id).ShouldBe(["Qwen3.8-27B-GGUF-UD-Q4_K_XL"]);
    }

    // The host can proxy cloud providers alongside what it runs itself; "Lemonade" has to mean
    // the box, so anything it marks as somebody else's is not offered.
    [Fact]
    public async Task AModelProxiedFromACloudProvider_IsNotOffered()
    {
        StubModels(200, Models(
            Model("local", ["chat", "tool-calling"], downloaded: true),
            Model("fireworks.kimi-k2p5", ["cloud", "chat", "tool-calling"], downloaded: true),
            Model("openai.gpt-5", ["chat", "tool-calling"], downloaded: true, provider: "openai"),
            Model("together.llama", ["chat", "tool-calling"], downloaded: true, recipe: "cloud")));
        var discovery = Discovery(_server.Url!);

        await discovery.RefreshAsync(CancellationToken.None);

        discovery.Current.Select(m => m.Id).ShouldBe(["local"]);
    }

    [Fact]
    public async Task TheVisionLabel_IsTheImageKind_AndNothingElseIsAccepted()
    {
        StubModels(200, Models(
            Model("sees", ["chat", "tool-calling", "vision"], downloaded: true),
            Model("reads", ["chat", "tool-calling"], downloaded: true)));
        var discovery = Discovery(_server.Url!);

        await discovery.RefreshAsync(CancellationToken.None);

        discovery.Current.Single(m => m.Id == "sees").AcceptedAttachmentKinds.ShouldBe([AttachmentKind.Image]);
        discovery.Current.Single(m => m.Id == "reads").AcceptedAttachmentKinds.ShouldBeEmpty();
    }

    // context_length is what the box loads the model with; max_context_window is what the model
    // could take. The loaded value is what a turn has to fit, so it wins when both are present.
    [Fact]
    public async Task TheContextWindow_IsTheLoadedLengthElseTheModelsMaximumElseUnknown()
    {
        StubModels(200, Models(
            Model("both", ["chat", "tool-calling"], downloaded: true, contextLength: 50000, maxContextWindow: 262144),
            Model("max-only", ["chat", "tool-calling"], downloaded: true, maxContextWindow: 131072),
            Model("neither", ["chat", "tool-calling"], downloaded: true)));
        var discovery = Discovery(_server.Url!);

        await discovery.RefreshAsync(CancellationToken.None);

        discovery.Current.Single(m => m.Id == "both").ContextWindow.ShouldBe(50000);
        discovery.Current.Single(m => m.Id == "max-only").ContextWindow.ShouldBe(131072);
        discovery.Current.Single(m => m.Id == "neither").ContextWindow.ShouldBeNull();
    }

    [Theory]
    [InlineData(503, "the box is having a moment")]
    [InlineData(200, "<html>not json</html>")]
    [InlineData(200, """{ "data": "not an array" }""")]
    public async Task AHostThatCannotBeAsked_OffersNothingAndWarnsOnce(int status, string body)
    {
        StubModels(status, body);
        var discovery = Discovery(_server.Url!);

        await discovery.RefreshAsync(CancellationToken.None);
        await discovery.RefreshAsync(CancellationToken.None);

        discovery.Current.ShouldBeEmpty();
        _warnings.Messages.ShouldHaveSingleItem().ShouldContain("Lemonade chat host");
    }

    [Fact]
    public async Task AnUnreachableHost_OffersNothingAndWarnsOnce()
    {
        var url = _server.Url!;
        _server.Stop();
        var discovery = Discovery(url);

        await discovery.RefreshAsync(CancellationToken.None);
        await discovery.RefreshAsync(CancellationToken.None);

        discovery.Current.ShouldBeEmpty();
        _warnings.Messages.ShouldHaveSingleItem().ShouldContain(url);
    }

    // A host that answered once and then went away offers nothing, not its last answer: a model
    // the box cannot serve must not stay in the menu.
    [Fact]
    public async Task AHostThatStopsAnswering_StopsOfferingWhatItLastSaid()
    {
        StubModels(200, Models(Model("local", ["chat", "tool-calling"], downloaded: true)));
        var discovery = Discovery(_server.Url!);
        await discovery.RefreshAsync(CancellationToken.None);
        discovery.Current.ShouldNotBeEmpty();

        _server.Reset();
        StubModels(503, "gone");
        await discovery.RefreshAsync(CancellationToken.None);

        discovery.Current.ShouldBeEmpty();
    }

    // Once per outage, not once per tick: a box that is off overnight is one line in the log, and
    // the next outage after it comes back is one more.
    [Fact]
    public async Task AWarning_IsRepeatedOnlyAfterTheHostAnsweredAgain()
    {
        StubModels(503, "down");
        var discovery = Discovery(_server.Url!);
        await discovery.RefreshAsync(CancellationToken.None);

        _server.Reset();
        StubModels(200, Models(Model("local", ["chat", "tool-calling"], downloaded: true)));
        await discovery.RefreshAsync(CancellationToken.None);

        _server.Reset();
        StubModels(503, "down again");
        await discovery.RefreshAsync(CancellationToken.None);
        await discovery.RefreshAsync(CancellationToken.None);

        _warnings.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AnEmptyAddress_AsksNobodyLogsNothingAndOffersNothing()
    {
        StubModels(200, Models(Model("local", ["chat", "tool-calling"], downloaded: true)));
        var discovery = Discovery("");

        await discovery.RefreshAsync(CancellationToken.None);

        discovery.Current.ShouldBeEmpty();
        _warnings.Messages.ShouldBeEmpty();
        _server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnApiKey_IsSentAsABearerToken_AndOmittedWhenBlank()
    {
        StubModels(200, Models());

        await Discovery(_server.Url!, apiKey: "secret").RefreshAsync(CancellationToken.None);
        await Discovery(_server.Url!, apiKey: "").RefreshAsync(CancellationToken.None);

        var headers = _server.LogEntries
            .Select(e => e.RequestMessage.Headers is { } h && h.TryGetValue("Authorization", out var value)
                ? value.FirstOrDefault()
                : null)
            .ToList();
        headers.ShouldBe(["Bearer secret", null]);
    }

    private LemonadeModelDiscovery Discovery(string url, string? apiKey = null)
    {
        var client = new HttpClient();
        _clients.Add(client);
        return new LemonadeModelDiscovery(
            client,
            new LemonadeChatHostOptions { ApiUrl = url == "" ? "" : $"{url}/api/v1", ApiKey = apiKey },
            LoggerFactory.Create(b => b.AddProvider(_warnings)).CreateLogger<LemonadeModelDiscovery>());
    }

    private static string Models(params object[] models) =>
        JsonSerializer.Serialize(new { data = models, @object = "list" });

    private static object Model(
        string id, string[] labels, bool downloaded, int? contextLength = null, int? maxContextWindow = null,
        string? provider = null, string recipe = "llamacpp")
    {
        var model = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["labels"] = labels,
            ["downloaded"] = downloaded,
            ["recipe"] = recipe,
            ["owned_by"] = "lemonade",
            ["object"] = "model"
        };
        if (contextLength is { } length)
        {
            model["context_length"] = length;
        }

        if (maxContextWindow is { } window)
        {
            model["max_context_window"] = window;
        }

        if (provider is not null)
        {
            model["provider"] = provider;
        }

        return model;
    }

    private void StubModels(int statusCode, string body) =>
        _server.Given(Request.Create().WithPath("/api/v1/models").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));
}