using Domain.DTOs;
using Domain.DTOs.Channel;
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