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

    public async Task LoadAsync(string? month)
    {
        switch (await _admin.GetUsageAsync(month))
        {
            case AdminAnswer<AdminUsage>.Answered answered:
                _dispatcher.Dispatch(new HouseholdUsageLoaded(answered.Value));
                break;
            case AdminAnswer<AdminUsage>.SignedOut:
                _accounts.SendToSignIn();
                break;
            case AdminAnswer<AdminUsage>.Refused refused:
                _dispatcher.Dispatch(new HouseholdUsageNotLoaded(
                    new AdminNotice(AdminNoticeKind.Refused, refused.Refusal.Message, refused.Refusal.Problems)));
                break;
            case AdminAnswer<AdminUsage>.NotAllowed notAllowed:
                _dispatcher.Dispatch(new HouseholdUsageNotLoaded(new AdminNotice(AdminNoticeKind.Unavailable, notAllowed.Message)));
                break;
            default:
                _dispatcher.Dispatch(new HouseholdUsageNotLoaded(new AdminNotice(AdminNoticeKind.Unavailable, UnreadableMessage)));
                break;
        }
    }

    public void Dispose() => _loadRegistration.Dispose();
}