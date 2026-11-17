using McpChannelSignalR.Hubs;
using McpChannelSignalR.Modules;
using McpChannelSignalR.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Tests.Integration.McpServers;
using SignalRSettings = McpChannelSignalR.Settings;

namespace Tests.Unit.McpChannelSignalR;

// The hub and the queue dispatcher send a person's turns and tell a topic of its queue through the same
// PersonTurns and QueueNews, built by the shipped registration. Resolved from that registration rather
// than hand-built, since a hand-built host stays green against a module that forgot one.
public sealed class ChatHubCompositionTests : IAsyncLifetime
{
    private readonly ServiceCollection _services = [];
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _services.AddLogging().ConfigureChannel(new SignalRSettings.ChannelSettings
        {
            RedisConnectionString = McpServerRegistrations.UnreachableRedis,
            Mcp = McpServerRegistrations.DeploymentGate
        });
        _provider = _services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return Task.CompletedTask;
    }

    [Fact]
    public void TheHubAndTheDispatcher_ShareOneOfEach()
    {
        _services.Count(d => d.ServiceType == typeof(PersonTurns)).ShouldBe(1);
        _services.Count(d => d.ServiceType == typeof(QueueNews)).ShouldBe(1);

        // Built as the host builds it; the other hosted services here need a running host.
        _services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(QueueDispatcher))
            .ShouldHaveSingleItem();
        ActivatorUtilities.CreateInstance<QueueDispatcher>(_provider).Dispose();
    }

    [Fact]
    public async Task TheHub_IsActivatedFromTheShippedRegistration()
    {
        await using var scope = _provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IHubActivator<ChatHub>>().Create().ShouldNotBeNull();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        foreach (var instance in _services.Select(d => d.ImplementationInstance).OfType<IDisposable>())
        {
            instance.Dispose();
        }
    }
}