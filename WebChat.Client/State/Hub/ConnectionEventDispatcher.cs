using WebChat.Client.State.Connection;

namespace WebChat.Client.State.Hub;

public sealed class ConnectionEventDispatcher(IDispatcher dispatcher)
{
    public void HandleConnecting()
    {
        dispatcher.Dispatch(new ConnectionConnecting());
    }

    public void HandleConnected()
    {
        dispatcher.Dispatch(new ConnectionConnected());
    }

    public void HandleReconnecting()
    {
        dispatcher.Dispatch(new ConnectionReconnecting());
    }

    public void HandleReconnected()
    {
        dispatcher.Dispatch(new ConnectionReconnected());
    }

    public void HandleClosed(Exception? exception)
    {
        dispatcher.Dispatch(new ConnectionClosed(exception?.Message));
    }

    public void HandleSessionRefused()
    {
        dispatcher.Dispatch(new HubSessionRefused());
    }
}