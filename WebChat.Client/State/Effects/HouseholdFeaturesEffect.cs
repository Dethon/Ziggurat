using Domain.DTOs.Household;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Admin;

namespace WebChat.Client.State.Effects;

// The admin area's features page asks the admin API through here: the household's features loaded
// when the page opens, and saved whole when the owner saves them.
public sealed class HouseholdFeaturesEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IAdminService _admin;
    private readonly IAccountService _accounts;
    private readonly IDisposable _loadRegistration;
    private readonly IDisposable _saveRegistration;

    public HouseholdFeaturesEffect(
        Dispatcher dispatcher, IAdminService admin, IAccountService accounts, ILogger<HouseholdFeaturesEffect> logger)
    {
        _dispatcher = dispatcher;
        _admin = admin;
        _accounts = accounts;

        _loadRegistration = dispatcher.RegisterHandler<LoadHouseholdFeatures>(
            _ => LoadAsync().LogFaults(logger, nameof(LoadHouseholdFeatures)));
        _saveRegistration = dispatcher.RegisterHandler<SaveHouseholdFeatures>(
            action => SaveAsync(action.Features, action.Loaded).LogFaults(logger, nameof(SaveHouseholdFeatures)));
    }

    public async Task LoadAsync() =>
        Settle(await _admin.GetFeaturesAsync(), answered => new HouseholdFeaturesLoaded(answered.Value));

    public async Task SaveAsync(FeaturesSection features, FeaturesSection? loaded = null) =>
        Settle(await _admin.SaveFeaturesAsync(features, loaded), answered => new HouseholdFeaturesSaved(answered.Value, answered.Warning));

    private void Settle(AdminAnswer<FeaturesSection> answer, Func<AdminAnswer<FeaturesSection>.Answered, IAction> outcome) =>
        _dispatcher.Settle(_accounts, answer, outcome, notice => new HouseholdFeaturesNotSaved(notice));

    public void Dispose()
    {
        _loadRegistration.Dispose();
        _saveRegistration.Dispose();
    }
}