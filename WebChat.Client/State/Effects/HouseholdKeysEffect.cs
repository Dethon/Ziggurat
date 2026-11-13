using Domain.DTOs.Admin;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Admin;

namespace WebChat.Client.State.Effects;

// The admin area's keys page asks the admin API through here: the keys' states loaded when the page
// opens, and each key saved on its own. A page signed out since it loaded is sent to sign in and back.
public sealed class HouseholdKeysEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IAdminService _admin;
    private readonly IAccountService _accounts;
    private readonly IDisposable[] _registrations;

    public HouseholdKeysEffect(
        Dispatcher dispatcher, IAdminService admin, IAccountService accounts, ILogger<HouseholdKeysEffect> logger)
    {
        _dispatcher = dispatcher;
        _admin = admin;
        _accounts = accounts;

        _registrations =
        [
            dispatcher.RegisterHandler<LoadHouseholdKeys>(_ => LoadAsync().LogFaults(logger, nameof(LoadHouseholdKeys))),
            dispatcher.RegisterHandler<SaveOpenRouterKey>(action =>
                SaveAsync(KeyForm.OpenRouter, _admin.SaveOpenRouterKeyAsync(action.Key)).LogFaults(logger, nameof(SaveOpenRouterKey))),
            dispatcher.RegisterHandler<SaveIdealistaKeys>(action =>
                SaveAsync(KeyForm.Idealista, _admin.SaveIdealistaKeysAsync(action.Keys)).LogFaults(logger, nameof(SaveIdealistaKeys))),
            dispatcher.RegisterHandler<SaveLemonadeBox>(action =>
                SaveAsync(KeyForm.Lemonade, _admin.SaveLemonadeBoxAsync(action.Box)).LogFaults(logger, nameof(SaveLemonadeBox))),
            dispatcher.RegisterHandler<RemoveLemonadeBox>(_ =>
                SaveAsync(KeyForm.Lemonade, _admin.RemoveLemonadeBoxAsync()).LogFaults(logger, nameof(RemoveLemonadeBox))),
            dispatcher.RegisterHandler<SaveTelegramBot>(action =>
                SaveAsync(KeyForm.Telegram, _admin.SaveTelegramBotAsync(action.AgentId, action.Bot))
                    .LogFaults(logger, nameof(SaveTelegramBot))),
            dispatcher.RegisterHandler<CheckHouseholdKey>(action => CheckAsync(action).LogFaults(logger, nameof(CheckHouseholdKey)))
        ];
    }

    public async Task LoadAsync() =>
        Settle(null, await _admin.GetKeysAsync(), answered => new HouseholdKeysLoaded(answered.Value));

    private async Task SaveAsync(KeyForm form, Task<AdminAnswer<HouseholdKeys>> saving) =>
        Settle(form, await saving, answered => new HouseholdKeysSaved(form, answered.Value, answered.Warning));

    private async Task CheckAsync(CheckHouseholdKey action) =>
        Settle(action.Form, await _admin.CheckKeyAsync(action.Form switch
        {
            KeyForm.OpenRouter => AdminProtocol.OpenRouterKeyCheck,
            KeyForm.Idealista => AdminProtocol.IdealistaKeysCheck,
            KeyForm.Lemonade => AdminProtocol.LemonadeBoxCheck,
            _ => AdminProtocol.TelegramBotCheck(action.AgentId ?? "")
        }), answered => new HouseholdKeyChecked(action.Form, answered.Value, answered.Warning));

    private void Settle(
        KeyForm? form, AdminAnswer<HouseholdKeys> answer, Func<AdminAnswer<HouseholdKeys>.Answered, IAction> outcome) =>
        _dispatcher.Settle(_accounts, answer, outcome, notice => new HouseholdKeysNotSaved(form, notice));

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
    }
}