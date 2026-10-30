using Domain.DTOs.Channel;

namespace Domain.Contracts;

// The browse server's one-time links, as the login tools ask for them. Where the links point, how
// long they live and what opening one does are the server's; who may be handed one is the tools'.
public interface ILoginHandoffs
{
    // Whether the deployment has a public address to build a link on.
    bool IsConfigured { get; }

    int LinkMinutes { get; }

    // Opens the url in a handoff tab of the person's context — the person being their account's user
    // id — and answers its link. With inChatClient, the link is also put in a new conversation of
    // the person's chat client, for a turn that cannot show a link itself. Throws
    // PersonStateUnreadableException when the person's saved logins will not open.
    Task<string> BeginAsync(
        string person, ConversationContext context, string url, bool inChatClient, CancellationToken ct);
}

public interface ILoginsPages
{
    // A link to the page listing the person's logins, for them alone, by their account's user id.
    Task<string> MintAsync(string person, CancellationToken ct);
}