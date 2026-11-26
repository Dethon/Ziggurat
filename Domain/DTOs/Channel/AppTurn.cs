using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// A built app's ask, as the apps server carries it on the message: which app asks, the instructions
// its repository answers with in place of the agent's persona, and the servers its asks may use, already
// narrowed to what the app may reach. Believed only on a message from the deployment's `apps` channel
// endpoint; anywhere else the agent drops it and the message is an ordinary turn.
[PublicAPI]
public record AppTurn
{
    public required string AppId { get; init; }

    public required string Instructions { get; init; }

    public IReadOnlyList<string> Servers { get; init; } = [];
}