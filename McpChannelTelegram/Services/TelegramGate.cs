using System.Globalization;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.Services;

// Who gets a turn. A message from a Telegram linked to an account is that account's turn, in a
// private chat or in a group bound to the same household; anybody else is answered and gets none.
// The ways in are answered here before anybody is looked up, since a stranger must be able to use
// them: a start parameter (an invite, or the account page's code), /web for a link that adds a
// passkey, and /bind with an owner's code in the group it binds.
public sealed partial class TelegramGate(TelegramAccess access, ITelegramAccounts accounts, ILogger<TelegramGate> logger)
{
    private const string Unlinked =
        "I don't know you yet. To use me, ask an owner of the household for an invite and open it here in "
        + "Telegram; if you have an account already, get a Telegram link code from your account page.";

    private const string CodeRefused =
        "That code can't be used: it has been used already, its week is up, or it was never one. ";

    // The account of the message's sender, or null when the message is not a turn: the gate has
    // answered it already, whether it was a way in, a refusal, or the accounts service not answering.
    public async Task<ResolvedAccount?> AdmitAsync(
        ITelegramBotClient bot, Message message, string content, CancellationToken ct)
    {
        try
        {
            var answer = message.From is { } from
                ? await AnswerCommandAsync(message, from, CommandOf(content), ct)
                : Unlinked;
            if (answer is not null)
            {
                await ReplyAsync(bot, message, answer, ct);
                return null;
            }

            var person = await access.PersonAsync(message.From!.Id, ct);
            var refusal = person is null
                ? Unlinked
                : message.Chat.Type == ChatType.Private
                    ? null
                    : await GroupRefusalAsync(message.Chat.Id, person, ct);
            if (refusal is not null)
            {
                await ReplyAsync(bot, message, refusal, ct);
                return null;
            }

            return person;
        }
        // An outage is not a stranger: nobody is told to ask for an invite, and nothing is
        // remembered about them, so their next message is answered as it should have been.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The accounts service did not answer for Telegram chat {ChatId}", message.Chat.Id);
            await ReplyAsync(bot, message, "I can't check who you are right now. Try again in a moment.", ct);
            return null;
        }
    }

    // The answer to one of the ways in, or null for anything else, which is then a turn or a refusal
    // like any message. A bare /start, which Telegram sends when someone first opens the bot, is not a
    // way in; nor is /web with words after it, which somebody meant as a turn.
    private async Task<string?> AnswerCommandAsync(Message message, User from, (string Name, string Argument)? command, CancellationToken ct) =>
        command switch
        {
            ("start", var code) when StartParameter().IsMatch(code) => await RedeemAsync(from.Id, code, ct),
            ("web", "") => message.Chat.Type == ChatType.Private
                ? await WebLinkAsync(from.Id, ct)
                : "Ask me for that in a private chat: whoever opens the link gets into your account.",
            ("bind", var code) => message.Chat.Type == ChatType.Private
                ? "Send /bind and the code in the group you want to bind, not here."
                : code.Length == 0
                    ? "Send /bind followed by the code an owner gets on the members page."
                    : await BindAsync(message.Chat.Id, code, ct),
            _ => null
        };

    private async Task<string> RedeemAsync(long telegramId, string code, CancellationToken ct)
    {
        var redeemed = await accounts.RedeemStartAsync(telegramId, code, ct);
        if (redeemed.Account is { } person)
        {
            access.Linked(telegramId, person);
        }

        return redeemed switch
        {
            { Outcome: TelegramStartOutcome.Invited, Account: { } invited } =>
                $"Welcome, {invited.DisplayName}! Your account is ready, and this Telegram is how I know you. "
                + "Send /web here whenever you want a link to sign in to the web apps with a passkey.",
            { Outcome: TelegramStartOutcome.Linked, Account: { } linked } =>
                $"Linked: this Telegram is {linked.DisplayName}'s now.",
            { Outcome: TelegramStartOutcome.TelegramTaken } =>
                "This Telegram is linked to an account already, so it can't take that code.",
            _ => CodeRefused + "Ask an owner for a new invite, or get a new code from your account page."
        };
    }

    private async Task<string> WebLinkAsync(long telegramId, CancellationToken ct) =>
        await accounts.MintWebLinkAsync(telegramId, ct) switch
        {
            { Outcome: TelegramWebLinkOutcome.Minted, Url: { } url, ExpiresAt: { } expiresAt } =>
                "This link adds a passkey to your account, so you can sign in to the web apps. It works once, until "
                + $"{expiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC:\n{url}",
            { Outcome: TelegramWebLinkOutcome.Refused } =>
                "The operator adds a passkey from their account page or with the accounts container's command, not through Telegram.",
            _ => Unlinked
        };

    private async Task<string> BindAsync(long chatId, string code, CancellationToken ct)
    {
        var bound = await accounts.BindGroupAsync(chatId, code, ct);
        if (bound.TenantId is { } tenantId)
        {
            access.Bound(chatId, tenantId);
        }

        return bound.Outcome switch
        {
            TelegramBindOutcome.Bound =>
                "This group is the household's now: its linked members' messages here are their own turns.",
            TelegramBindOutcome.BoundElsewhere => "This group belongs to another household.",
            _ => CodeRefused + "An owner gets a new one on the members page."
        };
    }

    // A group admits a linked person only once it is bound, and only to their own household.
    private async Task<string?> GroupRefusalAsync(long chatId, ResolvedAccount person, CancellationToken ct) =>
        await access.GroupTenantAsync(chatId, ct) switch
        {
            null => "This group isn't bound to a household, so I answer nobody here. "
                    + "An owner can bind it with /bind and a code from the members page.",
            var tenantId when tenantId != person.Account.TenantId => "This group belongs to another household than yours.",
            _ => null
        };

    private static Task ReplyAsync(ITelegramBotClient bot, Message message, string text, CancellationToken ct) =>
        bot.SendMessage(message.Chat.Id, text, replyParameters: message.MessageId, cancellationToken: ct);

    // "/start@ziggurat_bot CODE" is how a group sends a command picked from the bot's menu.
    private static (string Name, string Argument)? CommandOf(string content)
    {
        if (!content.StartsWith('/'))
        {
            return null;
        }

        var parts = content.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0
            ? null
            : (parts[0][1..].Split('@')[0].ToLowerInvariant(), parts.Length > 1 ? parts[1] : "");
    }

    // What Telegram passes on as a start parameter: up to 64 of these, and nothing else.
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex StartParameter();
}