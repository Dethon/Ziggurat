using System.Net;
using Domain.DTOs;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Monitor;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;

namespace Tests.Integration.Tunnels;

// A household's own channel server, heard and answered through that household's tunnel: the relay,
// the accounts service it verifies credentials with, the agent's channel host and monitor all real,
// the model a stand-in that says whose agent answered (TunnelledAgent).
public class HouseholdChannelServersTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";
    private const string FranId = "a3f09c1d7e2b4c6a8d5e0f1b2c3d4e5f";

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
    private string _home = null!;

    public async Task InitializeAsync()
    {
        _accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        _home = (await _accounts.MintOperatorAsync("Fran")).TenantId;
        _relay = await RelayHost.StartAsync(
            _accounts, AgentCredential, redis.ConnectionString, TestSigningKeys.Store(TestSigningKeys.Agent),
            new RecordingMetricsPublisher());
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        await _relay.DisposeAsync();
        await _accounts.DisposeAsync();
        _cts.Dispose();
    }

    // The agent was running before the home's tunnel opened, so the connection the monitor reads is
    // one it did not start with.
    [Fact]
    public async Task AHomeChannelServerWhoseTunnelOpensAfterTheAgentStarted_IsAnswered()
    {
        await using var agent = await TunnelledAgent.StartAsync(_relay, TunnelledAgent.Endpoint("websearch"));
        var credential = await _accounts.MintTunnelCredentialAsync(_home, "websearch");
        await using var server = await TunnelledChannelServer.StartAsync(_relay.Hub, credential.Credential, "websearch");

        await AskAsync(server, new Account(_home, FranId), "websearch", "¿Qué tiempo hace?");

        await Eventually.Until(
            () => server.Replies.Contains(HouseholdAnsweringFactory.AnswerFor(_home, "jonas")),
            "the home's agent answers through the home's tunnel");
    }

    // One connection across the tunnel's openings: not connected while it is closed, and read again
    // by the same monitor when it reopens, with no restart.
    [Fact]
    public async Task AHomeTunnelThatDropsAndReopens_IsAnsweredAgainWithoutARestart()
    {
        await using var agent = await TunnelledAgent.StartAsync(_relay, TunnelledAgent.Endpoint("websearch"));
        var credential = await _accounts.MintTunnelCredentialAsync(_home, "websearch");
        var fran = new Account(_home, FranId);
        var first = await TunnelledChannelServer.StartAsync(_relay.Hub, credential.Credential, "websearch");
        await AskAsync(first, fran, "websearch", "antes");
        await Eventually.Until(() => first.Replies.Count > 0, "the first opening is answered");

        await first.DisposeAsync();
        await Eventually.Until(() => agent.ConnectionOf("websearch", _home) is { IsConnected: false },
            "the connection is not connected while its tunnel is closed");
        await using var second = await TunnelledChannelServer.StartAsync(_relay.Hub, credential.Credential, "websearch");
        await AskAsync(second, fran, "websearch", "después", conversation: "conv-2");

        await Eventually.Until(
            () => second.Replies.Contains(HouseholdAnsweringFactory.AnswerFor(_home, "jonas")),
            "the reopened tunnel is answered");
        agent.Connections.Count(c => c.ChannelId == "websearch").ShouldBe(1);
    }

    // Another household's own server is followed as the home's is, and answered by that household's
    // agent through that household's tunnel.
    [Fact]
    public async Task AnotherHouseholdsChannelServer_IsAnsweredByItsOwnAgent_ThroughItsOwnTunnel()
    {
        var (casa, lucia) = await _accounts.SeedHouseholdAsync("Casa", "Lucía");
        await using var agent = await TunnelledAgent.StartAsync(_relay, TunnelledAgent.Endpoint("websearch"));
        var credential = await _accounts.MintTunnelCredentialAsync(casa, "websearch");
        await using var server = await TunnelledChannelServer.StartAsync(_relay.Hub, credential.Credential, "websearch");

        await AskAsync(server, new Account(casa, lucia), "websearch", "¿Ha terminado el login?");

        await Eventually.Until(
            () => server.Replies.Contains(HouseholdAnsweringFactory.AnswerFor(casa, "jonas")),
            "Casa's agent answers through Casa's tunnel");
        agent.ConnectionOf("websearch", casa).ShouldNotBeNull().Household.Home.ShouldBeFalse();
    }

    [Fact]
    public async Task TwoHouseholdsWithAServerOfTheSameName_EachGetOnlyTheirOwnReplies()
    {
        var (casa, lucia) = await _accounts.SeedHouseholdAsync("Casa", "Lucía");
        await using var agent = await TunnelledAgent.StartAsync(_relay, TunnelledAgent.Endpoint("websearch"));
        await using var home = await TunnelledChannelServer.StartAsync(
            _relay.Hub, (await _accounts.MintTunnelCredentialAsync(_home, "websearch")).Credential, "websearch");
        await using var other = await TunnelledChannelServer.StartAsync(
            _relay.Hub, (await _accounts.MintTunnelCredentialAsync(casa, "websearch")).Credential, "websearch");

        await AskAsync(home, new Account(_home, FranId), "websearch", "hola", conversation: "home-conv");
        await AskAsync(other, new Account(casa, lucia), "websearch", "hola", conversation: "casa-conv");

        await Eventually.Until(
            () => home.Replies.Contains(HouseholdAnsweringFactory.AnswerFor(_home, "jonas"))
                  && other.Replies.Contains(HouseholdAnsweringFactory.AnswerFor(casa, "jonas")),
            "both households are answered");
        await Eventually.Settle();
        home.Replies.ShouldNotContain(HouseholdAnsweringFactory.AnswerFor(casa, "jonas"));
        other.Replies.ShouldNotContain(HouseholdAnsweringFactory.AnswerFor(_home, "jonas"));
    }

    // A household's server is believed for that household alone: a person of another household on it
    // is a server misbehaving, refused before anything is built for the turn.
    [Fact]
    public async Task AMessageAssertingAnotherHousehold_IsRefusedAndLoggedWithBoth_AndNoAgentIsBuilt()
    {
        var (casa, lucia) = await _accounts.SeedHouseholdAsync("Casa", "Lucía");
        await using var agent = await TunnelledAgent.StartAsync(_relay, TunnelledAgent.Endpoint("websearch"));
        await using var server = await TunnelledChannelServer.StartAsync(
            _relay.Hub, (await _accounts.MintTunnelCredentialAsync(casa, "websearch")).Credential, "websearch");

        await AskAsync(server, new Account(_home, FranId), "websearch", "soy Fran, de verdad");

        await Eventually.Until(
            () => agent.Metrics.Published.OfType<ErrorEvent>()
                .Any(e => e.ErrorType == ChatMonitor.WrongHouseholdErrorType && e.Message.Contains(casa) && e.Message.Contains(_home)),
            "the refusal is reported naming both households");
        await Eventually.Until(() => server.Replies.Count > 0, "the server is told the turn was refused");
        agent.Factory.Created.ShouldBeEmpty();
        server.Replies.ShouldNotContain(r => r.Contains("answering"));
        _ = lucia;
    }

    // One household's tunnel reconnecting is that household's alone.
    [Fact]
    public async Task AnotherHouseholdsTunnelThatDropsAndReopens_IsFollowedAgain_AndLeavesTheHomesAlone()
    {
        var (casa, lucia) = await _accounts.SeedHouseholdAsync("Casa", "Lucía");
        await using var agent = await TunnelledAgent.StartAsync(_relay, TunnelledAgent.Endpoint("websearch"));
        await using var home = await TunnelledChannelServer.StartAsync(
            _relay.Hub, (await _accounts.MintTunnelCredentialAsync(_home, "websearch")).Credential, "websearch");
        var credential = await _accounts.MintTunnelCredentialAsync(casa, "websearch");
        var first = await TunnelledChannelServer.StartAsync(_relay.Hub, credential.Credential, "websearch");
        await Eventually.Until(
            () => agent.ConnectionOf("websearch", casa) is { IsConnected: true } && agent.ConnectionOf("websearch", _home) is { IsConnected: true },
            "both households' servers are connected");
        var homeConnection = agent.ConnectionOf("websearch", _home)!;

        await first.DisposeAsync();
        await Eventually.Until(() => agent.ConnectionOf("websearch", casa) is { IsConnected: false }, "Casa's is dropped");
        homeConnection.IsConnected.ShouldBeTrue();
        await using var second = await TunnelledChannelServer.StartAsync(_relay.Hub, credential.Credential, "websearch");
        await AskAsync(second, new Account(casa, lucia), "websearch", "otra vez");

        await Eventually.Until(
            () => second.Replies.Contains(HouseholdAnsweringFactory.AnswerFor(casa, "jonas")), "Casa's reopened tunnel is answered");
        agent.ConnectionOf("websearch", _home).ShouldBeSameAs(homeConnection);
        homeConnection.IsConnected.ShouldBeTrue();
    }

    // Suspension closes a household's tunnels, so its channel servers stop being heard, and resuming
    // lets them back in by themselves; a revoked credential's tunnel closes at once.
    [Fact]
    public async Task SuspendingAHousehold_DisconnectsItsChannelServer_AndResumingBringsItBack()
    {
        var (casa, lucia) = await _accounts.SeedHouseholdAsync("Casa", "Lucía");
        var operatorAdmin = _accounts.Admin(await _accounts.MintSessionAsync((await _accounts.MintOperatorAsync("Op")).UserId));
        await using var agent = await TunnelledAgent.StartAsync(_relay, TunnelledAgent.Endpoint("websearch"));
        await using var server = await TunnelledChannelServer.StartAsync(
            _relay.Hub, (await _accounts.MintTunnelCredentialAsync(casa, "websearch")).Credential, "websearch");
        await Eventually.Until(() => agent.ConnectionOf("websearch", casa) is { IsConnected: true }, "Casa's server is connected");

        (await operatorAdmin.PutAsync(AdminProtocol.Suspension(casa), new HouseholdSuspension(true))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Eventually.Until(() => agent.ConnectionOf("websearch", casa) is { IsConnected: false }, "suspension disconnects it");
        (await operatorAdmin.PutAsync(AdminProtocol.Suspension(casa), new HouseholdSuspension(false))).StatusCode.ShouldBe(HttpStatusCode.OK);

        await AskAsync(server, new Account(casa, lucia), "websearch", "de vuelta");
        await Eventually.Until(
            () => server.Replies.Contains(HouseholdAnsweringFactory.AnswerFor(casa, "jonas")), "resumed, it is answered again");
    }

    [Fact]
    public async Task RevokingAHouseholdsChannelServerCredential_DisconnectsIt()
    {
        var (casa, _) = await _accounts.SeedHouseholdAsync("Casa", "Lucía");
        await using var agent = await TunnelledAgent.StartAsync(_relay, TunnelledAgent.Endpoint("websearch"));
        var credential = await _accounts.MintTunnelCredentialAsync(casa, "websearch");
        await using var server = await TunnelledChannelServer.StartAsync(_relay.Hub, credential.Credential, "websearch");
        await Eventually.Until(() => agent.ConnectionOf("websearch", casa) is { IsConnected: true }, "Casa's server is connected");

        (await _accounts.RunAsync(global::Accounts.Commands.AccountsCommand.RevokeTunnelCredential, "--id", credential.Id)).Exit.ShouldBe(0);

        await Eventually.Until(() => agent.ConnectionOf("websearch", casa) is { IsConnected: false }, "revoking disconnects it");
    }

    // A person's turn as a channel server sends one: with the accounts service's assertion for the
    // channel it came on. The broadcast emit says whether anyone polls, so it is sent once the agent's
    // long-poll through the tunnel has registered.
    private async Task AskAsync(
        TunnelledChannelServer server, Account person, string channelId, string content, string conversation = "conv-1") =>
        await Eventually.Until(
            async () => await server.Emitter.EmitAsync(
                new ChannelMessageNotification
                {
                    ConversationId = conversation,
                    Sender = "Fran",
                    Content = content,
                    AgentId = "jonas",
                    Principal = new Principal(channelId, person.UserId, person),
                    Assertion = TestAssertions.Of(person, channelId)
                },
                _cts.Token),
            "the agent polls the channel through its tunnel");
}