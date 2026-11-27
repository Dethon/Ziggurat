using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Admin;

namespace WebChat.Client.State.Effects;

// The admin area's apps page asks the admin API through here: the household's caps on its apps' asks loaded
// when the page opens, and saved whole when the owner saves them.
public sealed class HouseholdAppAsksEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IAdminService _admin;
    private readonly IAccountService _accounts;
    private readonly IDisposable _loadRegistration;
    private readonly IDisposable _saveRegistration;

    public HouseholdAppAsksEffect(
        Dispatcher dispatcher, IAdminService admin, IAccountService accounts, ILogger<HouseholdAppAsksEffect> logger)
    {
        _dispatcher = dispatcher;
        _admin = admin;
        _accounts = accounts;

        _loadRegistration = dispatcher.RegisterHandler<LoadHouseholdAppAsks>(
            _ => LoadAsync().LogFaults(logger, nameof(LoadHouseholdAppAsks)));
        _saveRegistration = dispatcher.RegisterHandler<SaveHouseholdAppAsks>(
            action => SaveAsync(action.Caps, action.Loaded).LogFaults(logger, nameof(SaveHouseholdAppAsks)));
    }

    public async Task LoadAsync() =>
        Settle(await _admin.GetAppAsksAsync(), answered => new HouseholdAppAsksLoaded(answered.Value));

    public async Task SaveAsync(AppAsksSection caps, AppAsksSection? loaded = null) =>
        Settle(await _admin.SaveAppAsksAsync(caps, loaded), answered => new HouseholdAppAsksSaved(answered.Value, answered.Warning));

    private void Settle(AdminAnswer<AdminAppAsks> answer, Func<AdminAnswer<AdminAppAsks>.Answered, IAction> outcome) =>
        _dispatcher.Settle(_accounts, answer, outcome, notice => new HouseholdAppAsksNotSaved(notice));

    public void Dispose()
    {
        _loadRegistration.Dispose();
        _saveRegistration.Dispose();
    }
}