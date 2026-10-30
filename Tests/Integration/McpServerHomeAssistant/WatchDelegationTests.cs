using System.Net;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Agents.Mcp;
using Infrastructure.Clients.Channels;
using McpServerHomeAssistant.Modules;
using McpServerHomeAssistant.Services;
using McpServerHomeAssistant.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Eval.Fixtures;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerHomeAssistant;

// A watch acts for a person only because that person's turn set it: the agent's filesystem writes the
// watch through the real Home Assistant server, signing a delegation for the write as the turn's
// verified person; the server keeps it in the automation's metadata; the home fires the automation's
// callback the way its rest_command posts it; and the fire the agent's channel connection reads,
// carrying the delegation the server looked up, is checked the way the conversation group checks it.
public class WatchDelegationTests : IAsyncLifetime
{
    private const string Household = "4f1c9a7e2b6d4e0a9c3b8d5e7f1a2c4b";
    private const string WatchId = "laura-sugar-high";
    private const string Path = $"/watches/{WatchId}/watch.json";
    private const string Prompt = "Look into it and warn Fran.";

    private static readonly Account _fran = new(Household, "a3f09c1d7e2b4c6a8d5e0f1b2c3d4e5f");

    private static readonly string _watch = $$"""
        {"name":"Laura's sugar","triggers":[{"trigger":"numeric_state","entity_id":"sensor.glucosa_laura","above":180}],
         "effects":[{"kind":"prompt","prompt":"{{Prompt}}"}],"deliverTo":["telegram"]}
        """;

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private readonly int _port = TestPort.GetAvailable();
    private WebApplication _app = null!;

    private string Endpoint => $"http://localhost:{_port}/mcp";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, _port));
        builder.Services.ConfigureMcp(new McpSettings
        {
            HomeAssistant = new HomeAssistantConfiguration { BaseUrl = "http://home-assistant.test", Token = FakeHomeAssistant.Token },
            Announce = new AnnounceTokenSettings { Token = "secret" },
            Delivery = TestDelegations.Delivery,
            Mcp = McpTestSecret.Gate
        });
        var home = new FakeHomeAssistant();
        builder.Services.AddHttpClient(nameof(IHomeAssistantClient)).ConfigurePrimaryHttpMessageHandler(() => home);
        _app = builder.Build();
        _app.MapMcp("/mcp");
        WatchFiredEndpoint.Map(_app);
        await _app.StartAsync(_cts.Token);
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        TestPort.Release(_port);
        _cts.Dispose();
    }

    [Fact]
    public async Task AWatchAPersonSet_FiresAsThem()
    {
        await WriteAsync(TurnOf(_fran), backend => backend.CreateAsync(Path, _watch, false, true, CancellationToken.None));

        var fire = await FireAsync();

        (await TestDelegations.AdmitAsync(fire, "homeassistant")).ShouldBeOfType<FireAdmission.Delegated>()
            .Identity.Account.ShouldBe(_fran);
    }

    // Pausing it, rewording it, anything a turn acting for nobody writes leaves the watch nobody's.
    [Fact]
    public async Task AWatchRewrittenByATurnActingForNobody_FiresAsNobody()
    {
        await WriteAsync(TurnOf(_fran), backend => backend.CreateAsync(Path, _watch, false, true, CancellationToken.None));
        await WriteAsync(NobodysTurn(), backend => backend.EditAsync(Path,
            [new TextEdit("Look into it", "Look into it at once")], CancellationToken.None));

        var fire = await FireAsync("Look into it at once and warn Fran.");

        (await TestDelegations.AdmitAsync(fire, "homeassistant")).ShouldBe(new FireAdmission.Undelegated(null));
    }

    [Fact]
    public async Task AWatchWhoseDelegationTheAgentDidNotSign_IsRefused()
    {
        var watches = _app.Services.GetRequiredService<HaWatches>();
        var spec = HaWatchSpec.Parse(_watch);
        var forged = TestDelegations.Forged(_fran, ChannelIds.SignalR, WatchDelegation.OfWrite(Path, _watch, null, null)!);
        await watches.WriteAsync(WatchId, spec, "jonas", null, forged, CancellationToken.None);

        var fire = await FireAsync();

        (await TestDelegations.AdmitAsync(fire, "homeassistant"))
            .ShouldBe(new FireAdmission.Undelegated(nameof(TokenRefusal.UnknownKey)));
    }

    // The agent's key was lost: the watch's delegation verifies no more until the operator confirms
    // it again through the agent's redelegate, which rewrites the automation's metadata alone.
    [Fact]
    public async Task AWatchWhoseKeyWasLost_FiresAsItsWriterAgain_OnceRedelegated()
    {
        var watches = _app.Services.GetRequiredService<HaWatches>();
        var lost = TestDelegations.Forged(_fran, ChannelIds.SignalR, WatchDelegation.OfWrite(Path, _watch, null, null)!);
        await watches.WriteAsync(WatchId, HaWatchSpec.Parse(_watch), "jonas", null, lost, CancellationToken.None);
        var keys = TestSigningKeys.Store(TestSigningKeys.Agent);
        var output = new StringWriter();

        var exit = await new Redelegation(keys, new CallTokens(keys, TimeProvider.System), new StringReader("y\n"), output)
            .RedelegateAsync(
                [new Redelegation.Server("homeassistant", ct => McpClient.CreateAsync(McpTestSecret.Transport(Endpoint), cancellationToken: ct))],
                assumeYes: false, _cts.Token);

        exit.ShouldBe(0);
        output.ToString().ShouldContain($"watch {WatchId}");
        (await TestDelegations.AdmitAsync(await FireAsync(), "homeassistant")).ShouldBeOfType<FireAdmission.Delegated>()
            .Identity.Account.ShouldBe(_fran);
    }

    // The automation is the home's to edit: one whose callback delivers somewhere the watch never
    // said answers there as nobody.
    [Fact]
    public async Task AWatchFiringSomewhereItDoesNotDeliver_IsRefused()
    {
        await WriteAsync(TurnOf(_fran), backend => backend.CreateAsync(Path, _watch, false, true, CancellationToken.None));

        var fire = await FireAsync(deliverTo: "signalr");

        (await TestDelegations.AdmitAsync(fire, "homeassistant"))
            .ShouldBe(new FireAdmission.Undelegated(DelegationVerifier.NotThisFire));
    }

    private static ConversationContext TurnOf(Account person) =>
        new("jonas", "conv-1", person.UserId, new ReplyTarget(ChannelIds.SignalR, "conv-1"),
            Principal: new Principal(ChannelIds.SignalR, person.UserId, person),
            Identity: IdentityClaims.Person(person.UserId, person.TenantId, ChannelIds.SignalR, ["member"]));

    private static ConversationContext NobodysTurn() =>
        new("jonas", "sched-x", "scheduler", new ReplyTarget("scheduling", "sched-x"), OriginKind: MessageOriginKind.Schedule);

    private async Task WriteAsync<T>(ConversationContext turn, Func<IFileSystemBackend, Task<FsResult<T>>> write) where T : class
    {
        await using var client = await McpClient.CreateAsync(McpTestSecret.Transport(Endpoint), cancellationToken: _cts.Token);
        (await write(new McpFileSystemBackend(client, HaFileSystem.Name, null).As(turn)))
            .ShouldBeOfType<FsResult<T>.Ok>();
    }

    // The home's rest_command posting the watch's callback with the prompt as it rendered it, and the
    // message the agent's channel connection reads off the server for it.
    private async Task<ChannelMessageNotification> FireAsync(string prompt = Prompt, string deliverTo = "telegram")
    {
        await using var connection = new McpChannelConnection(
            "homeassistant", healthCheckInterval: TimeSpan.FromMilliseconds(200), mcpSecret: McpTestSecret.Value,
            callTokens: McpTestSecret.CallTokens);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var run = connection.RunAsync(Endpoint, () => [new AgentCatalogEntry("jonas", "Jonas", "the assistant")], stop.Token);
        var reading = connection.Messages.FirstAsync(stop.Token).AsTask();

        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
        http.DefaultRequestHeaders.Add(WatchFiredEndpoint.TokenHeader, "secret");
        await Eventually.Until(async () =>
        {
            var response = await http.PostAsJsonAsync(WatchFiredEndpoint.Path, new
            {
                watchId = WatchId,
                name = "Laura's sugar",
                agentId = "jonas",
                prompt,
                deliverTo = new[] { deliverTo },
                entityId = "sensor.glucosa_laura",
                toState = "183"
            }, _cts.Token);
            return response.StatusCode == HttpStatusCode.Accepted;
        }, "the agent's first poll to register it, so the fire is accepted");

        var message = await reading;
        await stop.CancelAsync();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }

        return new ChannelMessageNotification
        {
            ConversationId = message.ConversationId,
            Sender = message.Sender,
            Content = message.Content,
            AgentId = message.AgentId,
            UserId = message.UserId,
            Principal = message.Principal,
            Delegation = message.Delegation,
            ReplyTo = message.ReplyTo,
            Origin = message.Origin
        };
    }
}