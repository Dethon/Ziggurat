using System.Text.Json.Nodes;
using Domain.DTOs.Channel;

namespace Domain.Tools.Web;

// Who a turn is, for browsing as a person: the account its principal carries, or a refusal that
// names why there is nobody. A person is their account and nothing else — the name a channel goes
// by is never looked up — so whichever channel vouched for the account, the chat client, Telegram
// or a recognised voice, reaches the same browser context and logins. Nothing falls back, to the
// anonymous context or to another person, because a guess here is one person acting as another.
public abstract record PersonResolution
{
    // The account's user id is the key the person's browser context, live logins and sealed
    // record go by: opaque and issued by the accounts service, never a name somebody can pick.
    public sealed record Found(Account Account) : PersonResolution
    {
        public string Person => Account.UserId;
    }

    public sealed record Refused(JsonObject Error) : PersonResolution;

    public static PersonResolution Of(ConversationContext? context)
    {
        if (context is null)
        {
            return new Refused(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot tell who is asking."));
        }

        // A turn nobody sent is somebody only through the principal its server stamped, and a
        // principal stamped before accounts, or by a channel that knew nobody, carries none.
        var principal = Principal.Of(context);
        if (principal is { Account: { } account })
        {
            return new Found(account);
        }

        // The voice channel's guest — nobody the satellite recognised — is a principal with no
        // account, and on their own turn the person is still there to be told so.
        return principal is { ChannelId: ChannelIds.Voice } && context.OriginKind is null
            ? new Refused(ToolError.Create(
                ToolError.Codes.PermissionDenied,
                "The speaker's voice was not recognised, so there is no person to browse as.",
                $"Tell the person their voice was not recognised; they can ask again, or ask {WhereToAsk}."))
            : new Refused(ToolError.Create(
                ToolError.Codes.PermissionDenied,
                "No account stands behind this turn and nobody vouched for one, so there is no person to browse as.",
                context.OriginKind is null
                    ? $"Browse without asMe, and tell the person their own accounts are reached when they ask {WhereToAsk}."
                    : "Browse without asMe, and say in your report that this needs the person to ask you directly."));
    }

    // Where a person who is known can always be reached, for a refusal's hint.
    public const string WhereToAsk = "from Telegram or the chat client";
}