using Domain.Contracts;
using Domain.DTOs;
using Domain.Security;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

// Which messages are built apps' asks. The apps server is the one party that knows which app a turn is for
// and what it may use, and the agent believes that of the deployment's own connection to it alone — never a
// household's server of the same name, and never a fire. Whatever else carries the descriptor is an
// ordinary turn of its channel.
internal static class AppTurns
{
    public static bool IsAppsEndpoint(IHouseholdChannel channel) =>
        channel.ChannelId == AppChannels.Apps && HouseholdChannels.HouseholdOf(channel).IsEvery;

    // The message as the agent believes it. An ask is answered on its own channel alone: no review copy and
    // no other target, so an app's conversation is never minted as a topic anywhere.
    public static ChannelMessage Believed(IHouseholdChannel channel, ChannelMessage message, ILogger logger)
    {
        if (message.AppTurn is null)
        {
            return message;
        }

        if (IsAppsEndpoint(channel) && message.Origin is null)
        {
            return message with { ReviewCopy = null, ReplyTo = null };
        }

        logger.LogWarning(
            "Ignored the app turn of app {AppId} on {ChannelId} for conversation {ConversationId}: only the deployment's apps channel starts one",
            message.AppTurn.AppId, channel.ChannelId, message.ConversationId);
        return message with { AppTurn = null };
    }
}