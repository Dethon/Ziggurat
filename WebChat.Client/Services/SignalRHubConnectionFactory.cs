using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class SignalRHubConnectionFactory(
    IConfigService configService,
    NavigationManager navigationManager) : IHubConnectionFactory
{
    public async Task<IChatHubConnection> CreateAsync()
    {
        var config = await configService.GetConfigAsync();
        var isHttps = navigationManager.BaseUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // When on HTTPS (through reverse proxy), use same origin to go through the proxy
        // This avoids mixed content issues and allows the proxy to route SignalR properly
        var hubUrl = string.IsNullOrEmpty(config.AgentUrl) || isHttps
            ? navigationManager.ToAbsoluteUri("/hubs/chat").ToString()
            : $"{config.AgentUrl.TrimEnd('/')}/hubs/chat";

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new AggressiveRetryPolicy())
            .WithServerTimeout(TimeSpan.FromMinutes(6))
            .WithKeepAliveInterval(TimeSpan.FromSeconds(10))
            .Build();

        return new SignalRHubConnection(connection);
    }
}

internal sealed class SignalRHubConnection(HubConnection connection) : IChatHubConnection
{
    public HubConnectionState State => connection.State;

    public event Func<Exception?, Task>? Closed
    {
        add => connection.Closed += value;
        remove => connection.Closed -= value;
    }

    public event Func<Exception?, Task>? Reconnecting
    {
        add => connection.Reconnecting += value;
        remove => connection.Reconnecting -= value;
    }

    public event Func<string?, Task>? Reconnected
    {
        add => connection.Reconnected += value;
        remove => connection.Reconnected -= value;
    }

    public IDisposable On<T>(string methodName, Action<T> handler) => connection.On(methodName, handler);

    public Task StartAsync(CancellationToken cancellationToken = default) => connection.StartAsync(cancellationToken);

    public Task<bool> PingAsync(CancellationToken cancellationToken) =>
        connection.InvokeAsync<bool>("Ping", cancellationToken);

    // These answer rather than decide: whether the connection is live enough to carry a call
    // is settled once, by the live connection that owns this instance. Answering with a hub
    // result here keeps one vocabulary at the seam and lets a fake script not live.
    public async Task<HubResult<T>> InvokeAsync<T>(string methodName, params object?[] args) =>
        HubResult<T>.Answered(await connection.InvokeCoreAsync<T>(methodName, args));

    public async Task<HubResult<Nothing>> InvokeAsync(string methodName, params object?[] args)
    {
        await connection.InvokeCoreAsync(methodName, args);
        return HubResult<Nothing>.Answered(default);
    }

    // Opened rather than handed back lazy: the answer this returns is what the caller branches
    // on, so the transport has to have been reached before it is given one.
    public async Task<HubResult<IAsyncEnumerable<T>>> StreamAsync<T>(string methodName, params object?[] args) =>
        HubResult<IAsyncEnumerable<T>>.Answered(
            await HubStream.OpenAsync(connection.StreamAsyncCore<T>(methodName, args)));

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

internal sealed class AggressiveRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        // The hub's session check answering is not a server to wait for: no retry gets a
        // signed-out page or a suspended household back in, and every one asks the accounts
        // service again. Giving up closes the connection, which hands the page to the proxy.
        if (HubRefusal.Is(retryContext.RetryReason))
        {
            return null;
        }

        // First retry is immediate for fast mobile resume; subsequent retries
        // back off slightly to avoid hammering a temporarily unavailable server.
        return retryContext.PreviousRetryCount == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(1);
    }
}