namespace WebChat.Client.Contracts;

// The conversation the address the app was opened at names: a finished reply's notification, tapped,
// opens the app at its space with `?agent=&topic=`. Read once, by the first list the app loads.
public interface ILaunchLink
{
    LaunchTopic? Topic { get; }

    // Forgets it and takes it off the address, so a later reload opens as any other.
    void Spend();
}

// Topics are listed per agent, so opening one needs both.
public sealed record LaunchTopic(string AgentId, string TopicId);