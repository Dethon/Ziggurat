using Dashboard.Client.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace Dashboard.Client.Services;

public record ServiceHealthUpdate(string Service, bool IsHealthy, DateTimeOffset Timestamp, string? Household = null);

public sealed class SignalRMetricsHubConnection : IMetricsHubConnection
{
    private readonly HubConnection _connection;

    public SignalRMetricsHubConnection(Uri hubUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new MetricsRetryPolicy())
            .Build();
    }

    public event Func<Exception?, Task>? Closed
    {
        add => _connection.Closed += value;
        remove => _connection.Closed -= value;
    }

    public event Func<Exception?, Task>? Reconnecting
    {
        add => _connection.Reconnecting += value;
        remove => _connection.Reconnecting -= value;
    }

    public event Func<string?, Task>? Reconnected
    {
        add => _connection.Reconnected += value;
        remove => _connection.Reconnected -= value;
    }

    public IDisposable On<T>(string methodName, Func<T, Task> handler) => _connection.On(methodName, handler);

    public Task StartAsync(CancellationToken cancellationToken = default) => _connection.StartAsync(cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}