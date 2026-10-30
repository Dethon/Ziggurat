namespace Domain.DTOs.Channel;

// The agent's channel endpoint ids for the channels people speak through, as a turn's origin and a
// principal name the channel that vouched for them, and as the accounts service's resolution is asked
// about them, and as an assertion the accounts service signs names the channel it is for. Spelled
// once here, the accounts service included.
public static class ChannelIds
{
    // The chat client, whose hub is a SignalR server.
    public const string SignalR = "signalr";

    public const string Voice = "voice";

    public const string Telegram = "telegram";
}