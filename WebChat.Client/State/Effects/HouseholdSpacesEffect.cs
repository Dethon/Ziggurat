using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Admin;

namespace WebChat.Client.State.Effects;

// The admin area's spaces page asks the admin API through here: the household's spaces and the
// agents they may open on loaded when the page opens, and the spaces saved whole when the owner saves.
public sealed class HouseholdSpacesEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IAdminService _admin;
    private readonly IAccountService _accounts;
    private readonly IDisposable _loadRegistration;
    private readonly IDisposable _saveRegistration;

    public HouseholdSpacesEffect(
        Dispatcher dispatcher, IAdminService admin, IAccountService accounts, ILogger<HouseholdSpacesEffect> logger)
    {
        _dispatcher = dispatcher;
        _admin = admin;
        _accounts = accounts;

        _loadRegistration = dispatcher.RegisterHandler<LoadHouseholdSpaces>(
            _ => LoadAsync().LogFaults(logger, nameof(LoadHouseholdSpaces)));
        _saveRegistration = dispatcher.RegisterHandler<SaveHouseholdSpaces>(
            action => SaveAsync(action.Spaces, action.Loaded).LogFaults(logger, nameof(SaveHouseholdSpaces)));
    }

    public async Task LoadAsync() =>
        Settle(await _admin.GetSpacesAsync(), answered => new HouseholdSpacesLoaded(answered.Value));

    public async Task SaveAsync(SpacesSection spaces, SpacesSection? loaded = null) =>
        Settle(await _admin.SaveSpacesAsync(spaces, loaded), answered => new HouseholdSpacesSaved(answered.Value, answered.Warning));

    private void Settle(AdminAnswer<AdminSpaces> answer, Func<AdminAnswer<AdminSpaces>.Answered, IAction> outcome) =>
        _dispatcher.Settle(_accounts, answer, outcome, notice => new HouseholdSpacesNotSaved(notice));

    public void Dispose()
    {
        _loadRegistration.Dispose();
        _saveRegistration.Dispose();
    }
}