using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools.Web;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Integration.McpServers;

// The second gate on every server's /mcp, over real HTTP: a tool call must carry the agent's call
// token for this server. Holding the server's credential is no longer enough to act through it — a
// call with no token, an expired one, one signed for another server or with a key the agent does not
// publish is refused with the reason, and reported. What a server acts for is the token's identity,
// whatever a call writes into its _meta.
public class CallTokenGateTests : IAsyncLifetime
{
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(2);
    private const string Alice = "a11ce0000000000000000000000000a1";
    private const string Bob = "b0b0000000000000000000000000000b";

    private readonly RecordingMetrics _metrics = new();
    private RunningServer _server = null!;

    private sealed record GatedSettings(McpGateSettings Mcp) : IMcpHostSettings;

    public async Task InitializeAsync() =>
        _server = await InMemoryMcpServer.StartAsync(services => services
            .AddSingleton<IMetricsPublisher>(_metrics)
            .AddToolServer(new GatedSettings(McpTestSecret.Gate))
            .WithTools<WhoTools>());

    public async Task DisposeAsync() => await _server.DisposeAsync();

    public static TheoryData<string, TokenRefusal> Refusals => new()
    {
        { "unsigned", TokenRefusal.Missing },
        { "expired", TokenRefusal.Expired },
        { "another server's", TokenRefusal.WrongAudience },
        { "a foreign key's", TokenRefusal.UnknownKey }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ACallWithoutTheAgentsTokenForThisServer_IsRefusedWithItsReason(string token, TokenRefusal reason)
    {
        await using var client = await McpClient.CreateAsync(McpTestSecret.UnsignedTransport(_server.Endpoint));

        var refusal = await Should.ThrowAsync<McpProtocolException>(() => CallAsync(client, TokenFor(token), Turn()));

        refusal.Message.ShouldContain(reason.ToString());
        await Eventually.Until(() => _metrics.Errors.Any(e => e.Message.Contains(reason.ToString())), "the refusal is reported");
        _metrics.Errors.ShouldContain(e => e.ErrorType == "CallTokenRefused" && e.Service == McpTestSecret.Audience);
    }

    [Fact]
    public async Task APersonsToken_IsAdmittedAndActedOnAsThatPerson()
    {
        await using var client = await McpClient.CreateAsync(McpTestSecret.UnsignedTransport(_server.Endpoint));

        var who = await CallAsync(client, Sign(IdentityClaims.Person(Alice, "home", ChannelIds.SignalR, ["member"])), Turn());

        who["account"]!["userId"]!.GetValue<string>().ShouldBe(Alice);
        who["account"]!["tenantId"]!.GetValue<string>().ShouldBe("home");
        who["channel"]!.GetValue<string>().ShouldBe(ChannelIds.SignalR);
        who["browsesAs"]!.GetValue<string>().ShouldBe(Alice);
    }

    [Fact]
    public async Task AGuestsToken_IsAdmittedAsSomebodyWithNoAccount()
    {
        await using var client = await McpClient.CreateAsync(McpTestSecret.UnsignedTransport(_server.Endpoint));

        var who = await CallAsync(client, Sign(IdentityClaims.Guest("home", ChannelIds.Voice)), Turn(ChannelIds.Voice));

        who["account"].ShouldBeNull();
        who["channel"]!.GetValue<string>().ShouldBe(ChannelIds.Voice);
        who["browsesAs"].ShouldBeNull();
        who["conversation"]!.GetValue<string>().ShouldBe("conv-1");
    }

    // The forgery this gate exists for: a caller holding the credential writes somebody into _meta.
    // The token says who the call is for, so the _meta's principal is replaced, never believed — a
    // person's token acts for that person, and a token for nobody reaches nobody's account through
    // anything that asks who the call is.
    [Fact]
    public async Task APrincipalWrittenIntoTheMeta_IsIgnoredForTheTokens()
    {
        await using var client = await McpClient.CreateAsync(McpTestSecret.UnsignedTransport(_server.Endpoint));
        var forged = Turn() with { Principal = new Principal(ChannelIds.SignalR, Bob, new Account("home", Bob)) };

        var asAlice = await CallAsync(client, Sign(IdentityClaims.Person(Alice, "home", ChannelIds.SignalR, [])), forged);
        var asNobody = await CallAsync(client, Sign(IdentityClaims.Nobody(ChannelIds.SignalR)), forged);

        asAlice["account"]!["userId"]!.GetValue<string>().ShouldBe(Alice);
        asAlice["browsesAs"]!.GetValue<string>().ShouldBe(Alice);
        asNobody["account"].ShouldBeNull();
        asNobody["browsesAs"].ShouldBeNull();
    }

    // Only a tool call can act for anybody, so only a tool call is asked for a token: the handshake
    // and the tool list are what the agent asks while it builds a session, for nobody.
    [Fact]
    public async Task TheHandshakeAndTheToolList_PassOnTheCredentialAlone()
    {
        await using var client = await McpClient.CreateAsync(McpTestSecret.UnsignedTransport(_server.Endpoint));

        var tools = await client.ListToolsAsync();

        tools.ShouldContain(t => t.Name == "who");
        _metrics.Errors.ShouldBeEmpty();
    }

    // A test client signs as the agent's clients do, which is what every fixture relies on.
    [Fact]
    public async Task AClientSigningAsTheAgent_IsAdmitted()
    {
        await using var client = await McpClient.CreateAsync(McpTestSecret.Transport(_server.Endpoint));
        var turn = Turn() with { Principal = new Principal(ChannelIds.SignalR, Alice, new Account("home", Alice)) };

        var result = await client.CallToolAsync(new CallToolRequestParams { Name = "who", Meta = MetaOf(turn) });

        Parse(result)["account"]!["userId"]!.GetValue<string>().ShouldBe(Alice);
    }

    private static ConversationContext Turn(string channel = ChannelIds.SignalR) =>
        new("jonas", "conv-1", "fran", new ReplyTarget(channel, "conv-1"));

    private static string? TokenFor(string kind) => kind switch
    {
        "expired" => Sign(Someone, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10)),
        "another server's" => new TokenSigner(TestSigningKeys.Store(TestSigningKeys.Agent)).Sign(
            Someone.Stamped(TokenIssuers.Agent, "mcp-elsewhere", DateTimeOffset.UtcNow, _lifetime)),
        "a foreign key's" => new TokenSigner(TestSigningKeys.Store(TestSigningKeys.Accounts)).Sign(
            Someone.Stamped(TokenIssuers.Agent, McpTestSecret.Audience, DateTimeOffset.UtcNow, _lifetime)),
        _ => null
    };

    private static IdentityClaims Someone => IdentityClaims.Person(Alice, "home", ChannelIds.SignalR, []);

    private static string Sign(IdentityClaims identity) => Sign(identity, DateTimeOffset.UtcNow);

    private static string Sign(IdentityClaims identity, DateTimeOffset at) =>
        new TokenSigner(TestSigningKeys.Store(TestSigningKeys.Agent))
            .Sign(identity.Stamped(TokenIssuers.Agent, McpTestSecret.Audience, at, _lifetime));

    private static async Task<JsonObject> CallAsync(McpClient client, string? token, ConversationContext turn)
    {
        var meta = MetaOf(turn);
        if (token is not null)
        {
            meta[ChannelProtocol.CallTokenMetaKey] = token;
        }

        return Parse(await client.CallToolAsync(new CallToolRequestParams { Name = "who", Meta = meta }));
    }

    private static JsonObject MetaOf(ConversationContext turn) => new()
    {
        [ChannelProtocol.ConversationContextMetaKey] = JsonSerializer.SerializeToNode(turn, ChannelProtocol.SerializerOptions)
    };

    private static JsonObject Parse(CallToolResult result) =>
        JsonNode.Parse(InMemoryMcpServer.Text(result))!.AsObject();

    [McpServerToolType]
    public sealed class WhoTools
    {
        // Who the server takes the call to be for, through the accessor every tool reads it by, and
        // through something that asks the way a person's tools do.
        [McpServerTool(Name = "who")]
        [Description("Says who the call acts for.")]
        public static string Who(RequestContext<CallToolRequestParams> context)
        {
            var turn = context.Turn;
            var principal = Principal.Of(turn);
            return new JsonObject
            {
                ["conversation"] = turn?.ConversationId,
                ["channel"] = principal?.ChannelId,
                ["account"] = principal?.Account is { } account
                    ? new JsonObject { ["tenantId"] = account.TenantId, ["userId"] = account.UserId }
                    : null,
                ["browsesAs"] = PersonResolution.Of(turn) is PersonResolution.Found found ? found.Person : null
            }.ToJsonString();
        }
    }

    private sealed class RecordingMetrics : IMetricsPublisher
    {
        private readonly Lock _lock = new();
        private readonly List<ErrorEvent> _errors = [];

        public IReadOnlyList<ErrorEvent> Errors
        {
            get
            {
                lock (_lock)
                {
                    return [.. _errors];
                }
            }
        }

        public void Publish(MetricEvent metricEvent)
        {
            if (metricEvent is ErrorEvent error)
            {
                lock (_lock)
                {
                    _errors.Add(error);
                }
            }
        }
    }
}