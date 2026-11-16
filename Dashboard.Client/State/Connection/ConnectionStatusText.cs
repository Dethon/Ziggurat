namespace Dashboard.Client.State.Connection;

// One word and one modifier per status, written once: the badge in the layout's top bar, on every
// page, is the only place the status is shown.
public static class ConnectionStatusText
{
    extension(ConnectionStatus status)
    {
        public string Label => status switch
        {
            ConnectionStatus.Live => "Live",
            ConnectionStatus.Reconnecting => "Reconnecting",
            _ => "Connecting"
        };

        public string Modifier => status switch
        {
            ConnectionStatus.Live => "live",
            ConnectionStatus.Reconnecting => "reconnecting",
            _ => "connecting"
        };
    }
}