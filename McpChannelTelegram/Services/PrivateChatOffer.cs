using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace McpChannelTelegram.Services;

// Whatever is put to a person unprompted — an alert, a question another channel asked — goes to their
// private chat by the first bot that can write there, in the order BotRegistry.BotsPreferring gives:
// a bot cannot write first to someone who never opened a chat with it. False when no bot could.
public static class PrivateChatOffer
{
    public static async Task<bool> ShowAsync(
        IReadOnlyList<ITelegramBotClient> bots, Func<ITelegramBotClient, Task<bool>> show)
    {
        // A loop, since the next bot is tried only when this one refused, and an outage ends the walk.
        foreach (var bot in bots)
        {
            try
            {
                return await show(bot);
            }
            catch (ApiRequestException)
            {
                // Never opened a chat with this bot; the next may have one.
            }
            catch (HttpRequestException)
            {
                // An accounts service that could not say which chat is theirs shows it to nobody.
                return false;
            }
        }

        return false;
    }
}