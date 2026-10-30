namespace WebChat.Client.State.Connection;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

public sealed record ConnectionState
{
    public ConnectionStatus Status { get; init; } = ConnectionStatus.Disconnected;
    public DateTime? LastConnected { get; init; }
    public int ReconnectAttempts { get; init; }
    public string? Error { get; init; }

    // How many times the client has become live. Interruption is counted rather than
    // observed because a rebuild can start and finish without anyone seeing a disconnected
    // status in between, and a catch-up decision made on the status alone loses that race.
    public int Epoch { get; init; }

    public static ConnectionState Initial => new();
}