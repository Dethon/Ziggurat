using Domain.DTOs.Channel;

namespace Domain.Contracts;

// The browse server's one-time links, as the login tools ask for them. Where the links point, how
// long they live and what opening one does are the server's; who may be handed one is the tools'.
public interface ILoginHandoffs
{
    // Whether the deployment has a public address to build a link on.
    bool IsConfigured { get; }

    int LinkMinutes { get; }

    // Opens the url in a handoff tab of the person's context and answers its link. With a WebChat
    // name, the link is also put in a new WebChat conversation of that person's, for a turn that
    // cannot show a link itself. Throws PersonStateUnreadableException when the person's saved
    // logins will not open.
    Task<string> BeginAsync(
        string person, ConversationContext context, string url, string? inWebChatOf, CancellationToken ct);
}

public interface ILoginsPages
{
    // A link to the page listing the person's logins, for them alone.
    Task<string> MintAsync(string person, CancellationToken ct);
}