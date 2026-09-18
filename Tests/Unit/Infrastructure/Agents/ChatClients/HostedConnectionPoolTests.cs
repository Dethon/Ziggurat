using Agent.Modules;
using Domain.Contracts;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class HostedConnectionPoolTests
{
    // Real traffic is about 35 turns a day, so an ordinary gap between two turns is tens of
    // minutes. Anything shorter than this leaves the connection dead before the next turn.
    private static readonly TimeSpan _ordinaryGapBetweenTurns = TimeSpan.FromMinutes(3);

    [Fact]
    public void SharedHandler_OutlivesAnOrdinaryGapBetweenTurns()
    {
        var handler = OpenRouterChatClient.SharedHandler;

        handler.PooledConnectionLifetime.ShouldBe(HostedConnectionPool.ConnectionLifetime);
        handler.PooledConnectionIdleTimeout.ShouldBe(HostedConnectionPool.IdleTimeout);
        handler.PooledConnectionIdleTimeout.ShouldBeGreaterThan(_ordinaryGapBetweenTurns);
        handler.PooledConnectionLifetime.ShouldBeGreaterThan(handler.PooledConnectionIdleTimeout);
    }

    // A connection past its lifetime is recycled by whichever request arrives first, a keep-alive
    // ping or a user's turn, so the pool sits cold for up to one ping interval per lifetime. From
    // prod a cold handshake alone outlasts the skill preload's deadline, so that window has to be
    // rare: a fraction of turns small enough that a cold first turn is a matter of days, not hours.
    [Fact]
    public void ConnectionLifetime_KeepsTheColdWindowUnderOnePercentOfTurns()
    {
        var interval = HostedConnectionKeepAliveOptions.DefaultInterval;
        var coldFraction = interval / (HostedConnectionPool.ConnectionLifetime + interval);

        coldFraction.ShouldBeLessThan(0.01);
    }

    [Fact]
    public void EmbeddingClient_GetsTheSameConnectionPoolTreatmentAsTheChatClients()
    {
        var services = new ServiceCollection();
        services.AddMemory(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(IEmbeddingService));

        var primary = PrimaryHandlerOf(handler);
        primary.PooledConnectionLifetime.ShouldBe(HostedConnectionPool.ConnectionLifetime);
        primary.PooledConnectionIdleTimeout.ShouldBe(HostedConnectionPool.IdleTimeout);
    }

    private static SocketsHttpHandler PrimaryHandlerOf(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler
                ?? throw new InvalidOperationException("Handler chain ended without a primary handler");
        }

        return handler as SocketsHttpHandler
            ?? throw new InvalidOperationException($"Primary handler is {handler.GetType().Name}, not SocketsHttpHandler");
    }
}