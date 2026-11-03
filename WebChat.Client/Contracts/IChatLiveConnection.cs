namespace WebChat.Client.Contracts;

public interface IChatLiveConnection : IAsyncDisposable
{
    Task ConnectAsync();
    Task ReconnectIfNeededAsync();

    // For a caller holding something the person already did and cannot redo — a file they picked
    // — rather than one deciding whether to try. It waits out a reconnect instead of skipping it.
    Task<bool> EnsureLiveAsync();

    // A fresh connection whatever the state of this one: what the hub knows of the person — their
    // roles, whether they see all conversations — is read off the assertion the proxy signs as one opens.
    Task RenewAsync();

    Task<HubResult<T>> InvokeAsync<T>(string methodName, params object?[] args);
    Task<HubResult<Nothing>> InvokeAsync(string methodName, params object?[] args);
    Task<HubResult<IAsyncEnumerable<T>>> StreamAsync<T>(string methodName, params object?[] args);
}