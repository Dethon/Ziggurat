using Domain.DTOs.Channel;
using WebChat.Client.Models;

namespace WebChat.Client.Components.Chat;

// A turn a service started carries the model's instructions as its message, sent under the service's
// name (ServiceSenders) rather than a person's account. The chat draws it as one line saying what
// happened, with the instructions behind a disclosure, rather than as if somebody had written them. A
// person's message always carries their account id, so a person whose name happens to be a service's
// is drawn as themselves. An app that composes a request for its person (the Sugary app's turns carry
// their account) is known by what the request asks for: a structured answer, which no typed message
// asks for.
public static class ServiceNotice
{
    public static string? Of(ChatMessageModel message)
    {
        if (message.Role != "user")
        {
            return null;
        }

        if (message.ResponseFormat is { } format)
        {
            return StructuredAnswers.AppOf(format) is { } app ? $"Sent from the {app}" : "Sent from an app";
        }

        var service = message.SenderId is null or "" ? message.SenderName : message.SenderId;
        return service switch
        {
            ServiceSenders.Login => "You finished logging in",
            ServiceSenders.Scheduler => "A scheduled task ran",
            ServiceSenders.Watch => "A home watch fired",
            ServiceSenders.SugaryApp => "Sent from the Sugary app",
            ChannelProtocol.SystemSender => "A notice from the system",
            _ => null
        };
    }
}