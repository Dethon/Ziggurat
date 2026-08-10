namespace WebChat.Client.Contracts;

public interface IChatLiveConnection : IAsyncDisposable
{
    Task ConnectAsync();
    Task ReconnectIfNeededAsync();

    // For a caller holding something the person already did and cannot redo — a file they picked
    // — rather than one deciding whether to try. It waits out a reconnect instead of skipping it.
    Task<bool> EnsureLiveAsync();

    Task<HubResult<T>> InvokeAsync<T>(string methodName, params object?[] args);
    Task<HubResult<Nothing>> InvokeAsync(string methodName, params object?[] args);
    Task<HubResult<IAsyncEnumerable<T>>> StreamAsync<T>(string methodName, params object?[] args);
}