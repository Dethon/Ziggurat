using System.ComponentModel;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Channels;

// The capability probe asks "does this server offer this tool". Nothing can change that answer
// while a connection is up — every server in this repo registers its tools before its transport
// starts — so it is asked once per connection generation. Driven against a real server over
// loopback, because the count is only meaningful if the round trips are real.
// See docs/adr/0012-a-servers-tool-set-is-fixed-for-a-connection-generation.md.
public class McpChannelConnectionToolCacheTests
{
    private static int _listToolsCalls;

    [Fact]
    public async Task CreateConversation_TwiceOnOneGeneration_AsksForTheToolSetOnce()
    {
        Interlocked.Exchange(ref _listToolsCalls, 0);
        await using var server = await StartServerAsync();
        await using var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);

        var before = Volatile.Read(ref _listToolsCalls);

        (await CreateAsync(connection)).ShouldBe("conv-1");
        (await CreateAsync(connection)).ShouldBe("conv-1");

        (Volatile.Read(ref _listToolsCalls) - before).ShouldBe(1);
    }

    [Fact]
    public async Task CreateConversation_AfterAReconnect_AsksAgain()
    {
        // An operator redeploying a channel server with a new tool must see it once the agent
        // reconnects: a cached answer may never outlive the process that gave it.
        Interlocked.Exchange(ref _listToolsCalls, 0);
        await using var server = await StartServerAsync();
        await using var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);

        var before = Volatile.Read(ref _listToolsCalls);
        await CreateAsync(connection);

        await connection.ReconnectAsync(server.Endpoint, CancellationToken.None);
        await CreateAsync(connection);

        (Volatile.Read(ref _listToolsCalls) - before).ShouldBe(2);
    }

    [Fact]
    public async Task CreateConversation_AProbeFinishingAfterReconnect_DoesNotResurrectTheOldToolSet()
    {
        // The tool set is per connection generation. A probe still in flight when the connection
        // moves to a new generation must not store the old generation's answer, or a server
        // redeployed with a new tool would stay invisible for the life of the new connection —
        // exactly what the reconnect is meant to fix.
        using var oldProbeArrived = new SemaphoreSlim(0);
        using var oldProbeHeld = new SemaphoreSlim(0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var oldServer = await InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<NoConversationTools>()
            .WithRequestFilters(filters => filters.AddListToolsFilter(next => async (context, ct) =>
            {
                oldProbeArrived.Release();
                await oldProbeHeld.WaitAsync(ct);
                return await next(context, ct);
            })));
        await using var newServer = await StartServerAsync();
        await using var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(oldServer.Endpoint, CancellationToken.None);

        var staleCreate = CreateAsync(connection);
        await oldProbeArrived.WaitAsync(cts.Token);

        await connection.ConnectAsync(newServer.Endpoint, CancellationToken.None);
        oldProbeHeld.Release();
        (await staleCreate).ShouldBeNull();

        (await CreateAsync(connection)).ShouldBe("conv-1");
    }

    [Fact]
    public async Task CreateConversation_AProbeLandingWhileTheNextGenerationDials_DoesNotBecomeItsToolSet()
    {
        // The same rule one step earlier: the old generation's probe finishes while the connection
        // is mid-dial, before there is a new client to compare it against. What it learned belongs
        // to the generation it asked, so it must not be waiting in the cache for the one that
        // arrives a moment later — the redeployed server's new tool would stay invisible for the
        // life of the new connection.
        using var oldProbeArrived = new SemaphoreSlim(0);
        using var oldProbeHeld = new SemaphoreSlim(0);
        using var newServerHeld = new SemaphoreSlim(0);
        var gateNewServer = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var oldServer = await InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<NoConversationTools>()
            .WithRequestFilters(filters => filters.AddListToolsFilter(next => async (context, ct) =>
            {
                oldProbeArrived.Release();
                await oldProbeHeld.WaitAsync(ct);
                return await next(context, ct);
            })));
        // The gate opens for the server's own start-up client and closes behind it, so only the
        // connection under test is held mid-dial.
        await using var newServer = await InMemoryMcpServer.StartAsync(services => services
            .AddSingleton<IStartupFilter>(new GateFilter(newServerHeld, () => gateNewServer))
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<ConversationTools>());
        gateNewServer = true;

        await using var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(oldServer.Endpoint, CancellationToken.None);

        var staleCreate = CreateAsync(connection);
        await oldProbeArrived.WaitAsync(cts.Token);

        var dialing = connection.ConnectAsync(newServer.Endpoint, cts.Token);
        oldProbeHeld.Release();
        (await staleCreate).ShouldBeNull();

        newServerHeld.Release(int.MaxValue / 2);
        await dialing;

        (await CreateAsync(connection)).ShouldBe("conv-1");
    }

    // Holds every request until the test lets it through, so a connection can be pinned mid-dial.
    private sealed class GateFilter(SemaphoreSlim gate, Func<bool> armed) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                if (armed())
                {
                    await gate.WaitAsync(context.RequestAborted);
                }

                await proceed(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task CreateConversation_RacingTheClientDisposal_YieldsNull()
    {
        // A reconnect disposes the client while a create can still be in flight. What the disposed
        // client throws must not escape: DeliveryTargetResolver reads null as "this channel minted
        // nothing" and moves to the next target, per ADR 0011.
        await using var server = await StartServerAsync();
        var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);
        await connection.DisposeAsync();

        (await CreateAsync(connection)).ShouldBeNull();
    }

    [Fact]
    public async Task CreateConversation_AfterAReconnectThatCouldNotDial_YieldsNull()
    {
        // A failed reconnect leaves the connection with no client and no cached tool set, so the
        // capability probe runs against exactly the state ReconnectAsync leaves behind. Every
        // operation works from one snapshot of the client, so this answers null (ADR 0011) rather
        // than faulting on a field that was read a second time.
        await using var server = await StartServerAsync();
        await using var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);
        (await CreateAsync(connection)).ShouldBe("conv-1");

        await Should.ThrowAsync<Exception>(
            () => connection.ReconnectAsync("http://localhost:1/mcp", CancellationToken.None));

        (await CreateAsync(connection)).ShouldBeNull();
        await Should.NotThrowAsync(() => connection.RegisterAgentsAsync(
            [new AgentCatalogEntry("jonas", "Jonas", null)], CancellationToken.None));
        (await connection.IsHealthyAsync(CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateConversation_ServerWithoutTheTool_StillYieldsNull()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<NoConversationTools>());
        await using var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);

        (await CreateAsync(connection)).ShouldBeNull();
    }

    private static Task<string?> CreateAsync(McpChannelConnection connection) =>
        connection.CreateConversationAsync(
            "agent-1", "topic", "sender", initialPrompt: null, address: null,
            existingConversationId: null, principal: null, CancellationToken.None);

    private static Task<RunningServer> StartServerAsync() => InMemoryMcpServer.StartAsync(services => services
        .AddMcpServer()
        .WithHttpTransport()
        .WithTools<ConversationTools>()
        .WithRequestFilters(filters => filters.AddListToolsFilter(next => (context, ct) =>
        {
            Interlocked.Increment(ref _listToolsCalls);
            return next(context, ct);
        })));

    [McpServerToolType]
    public sealed class ConversationTools
    {
        [McpServerTool(Name = ChannelProtocol.CreateConversationTool)]
        [Description("Mint a conversation.")]
        public static string Create(
            string agentId, string topicName, string sender,
            string? initialPrompt, string? address, string? existingConversationId) => "conv-1";
    }

    [McpServerToolType]
    public sealed class NoConversationTools
    {
        [McpServerTool(Name = "unrelated")]
        [Description("A server that mints nothing.")]
        public static string Unrelated() => "ok";
    }
}