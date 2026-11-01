using Domain.DTOs.Household;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Admin;

namespace WebChat.Client.State.Effects;

// The admin area's settings page asks the admin API through here: the household's settings loaded
// when the page opens, and saved when the owner saves them. A page signed out since it loaded is
// sent to sign in and back, as the chat is.
public sealed class HouseholdSettingsEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IAdminService _admin;
    private readonly IAccountService _accounts;
    private readonly IDisposable _loadRegistration;
    private readonly IDisposable _saveRegistration;

    public HouseholdSettingsEffect(
        Dispatcher dispatcher, IAdminService admin, IAccountService accounts, ILogger<HouseholdSettingsEffect> logger)
    {
        _dispatcher = dispatcher;
        _admin = admin;
        _accounts = accounts;

        _loadRegistration = dispatcher.RegisterHandler<LoadHouseholdSettings>(
            _ => LoadAsync().LogFaults(logger, nameof(LoadHouseholdSettings)));
        _saveRegistration = dispatcher.RegisterHandler<SaveHouseholdSettings>(
            action => SaveAsync(action.Settings).LogFaults(logger, nameof(SaveHouseholdSettings)));
    }

    public async Task LoadAsync() =>
        Settle(await _admin.GetSettingsAsync(), answered => new HouseholdSettingsLoaded(answered.Value));

    public async Task SaveAsync(HouseholdSettings settings) =>
        Settle(await _admin.SaveSettingsAsync(settings), answered => new HouseholdSettingsSaved(answered.Value, answered.Warning));

    private void Settle(AdminAnswer<HouseholdSettings> answer, Func<AdminAnswer<HouseholdSettings>.Answered, IAction> outcome)
    {
        switch (answer)
        {
            case AdminAnswer<HouseholdSettings>.Answered answered:
                _dispatcher.Dispatch(outcome(answered));
                break;
            case AdminAnswer<HouseholdSettings>.SignedOut:
                _accounts.SendToSignIn();
                break;
            case AdminAnswer<HouseholdSettings>.Refused refused:
                _dispatcher.Dispatch(new HouseholdSettingsNotSaved(
                    new AdminNotice(AdminNoticeKind.Refused, refused.Refusal.Message, refused.Refusal.Problems)));
                break;
            case AdminAnswer<HouseholdSettings>.NotAllowed notAllowed:
                _dispatcher.Dispatch(new HouseholdSettingsNotSaved(new AdminNotice(AdminNoticeKind.Unavailable, notAllowed.Message)));
                break;
            default:
                _dispatcher.Dispatch(new HouseholdSettingsNotSaved(
                    new AdminNotice(AdminNoticeKind.Unavailable, AdminNotice.UnreachableMessage)));
                break;
        }
    }

    public void Dispose()
    {
        _loadRegistration.Dispose();
        _saveRegistration.Dispose();
    }
}