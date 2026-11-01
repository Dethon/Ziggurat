using Domain.DTOs.Admin;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Admin;

namespace WebChat.Client.State.Effects;

// The admin area's usage page asks the admin API through here, for the month the owner picks. The
// figures are the deployment's metrics host's, which the admin API asks: unanswered, the page says they
// could not be read rather than showing a month with nothing used. A page signed out since it loaded is
// sent to sign in and back, as the chat is.
public sealed class HouseholdUsageEffect : IDisposable
{
    public const string UnreadableMessage = "The household's usage could not be read just now. Try again in a moment.";

    private readonly Dispatcher _dispatcher;
    private readonly IAdminService _admin;
    private readonly IAccountService _accounts;
    private readonly IDisposable _loadRegistration;

    public HouseholdUsageEffect(
        Dispatcher dispatcher, IAdminService admin, IAccountService accounts, ILogger<HouseholdUsageEffect> logger)
    {
        _dispatcher = dispatcher;
        _admin = admin;
        _accounts = accounts;

        _loadRegistration = dispatcher.RegisterHandler<LoadHouseholdUsage>(
            action => LoadAsync(action.Month).LogFaults(logger, nameof(LoadHouseholdUsage)));
    }

    public async Task LoadAsync(string? month) =>
        _dispatcher.Settle(_accounts, await _admin.GetUsageAsync(month),
            answered => new HouseholdUsageLoaded(answered.Value), notice => new HouseholdUsageNotLoaded(notice), UnreadableMessage);

    public void Dispose() => _loadRegistration.Dispose();
}