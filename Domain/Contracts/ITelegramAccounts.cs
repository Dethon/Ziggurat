using Domain.DTOs.Channel;

namespace Domain.Contracts;

// What the Telegram bot asks the accounts service beyond whose an identity is (IAccountResolver),
// with the same credential: a start parameter redeemed for the Telegram that sent it, a passkey link
// for a linked person, a group bound by an owner's code, and which household a group is bound to.
// An outage throws, as a resolution's does, so the bot never tells a person a code was refused when
// nobody answered.
public interface ITelegramAccounts
{
    Task<TelegramStart> RedeemStartAsync(long telegramId, string code, CancellationToken ct);

    Task<TelegramWebLink> MintWebLinkAsync(long telegramId, CancellationToken ct);

    Task<TelegramBind> BindGroupAsync(long chatId, string code, CancellationToken ct);

    // The household a group is bound to, or null for a group bound to none.
    Task<string?> GroupTenantAsync(long chatId, CancellationToken ct);

    // The Telegram an account linked — its private chat with the bot — or null for one with none.
    Task<long?> TelegramIdOfAsync(Account account, CancellationToken ct);
}