using Domain.DTOs.Admin;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Admin;

namespace WebChat.Client.State.Effects;

// The admin area's servers page asks the admin API through here: the household's servers loaded when
// the page opens, a minted credential answered alone — the list is asked again after it, so the new
// server shows — and a revocation answered with the servers as they are now. A page signed out since
// it loaded is sent to sign in and back, as the chat is.
public sealed class HouseholdServersEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IAdminService _admin;
    private readonly IAccountService _accounts;
    private readonly IReadOnlyList<IDisposable> _registrations;

    public HouseholdServersEffect(
        Dispatcher dispatcher, IAdminService admin, IAccountService accounts, ILogger<HouseholdServersEffect> logger)
    {
        _dispatcher = dispatcher;
        _admin = admin;
        _accounts = accounts;

        _registrations =
        [
            dispatcher.RegisterHandler<LoadHouseholdServers>(_ =>
                LoadAsync().LogFaults(logger, nameof(LoadHouseholdServers))),
            dispatcher.RegisterHandler<AddHouseholdServer>(action =>
                AddAsync(action.Addition).LogFaults(logger, nameof(AddHouseholdServer))),
            dispatcher.RegisterHandler<RevokeServerCredential>(action =>
                RevokeAsync(action.CredentialId).LogFaults(logger, nameof(RevokeServerCredential)))
        ];
    }

    public async Task LoadAsync() =>
        Settle(await _admin.GetServersAsync(), answered => new HouseholdServersLoaded(answered.Value));

    private async Task AddAsync(ServerAddition addition)
    {
        if (Settle(await _admin.AddServerAsync(addition), answered => new ServerCredentialMinted(answered.Value)))
        {
            await LoadAsync();
        }
    }

    private async Task RevokeAsync(string credentialId) =>
        Settle(await _admin.RevokeServerCredentialAsync(credentialId),
            answered => new HouseholdServersSaved(answered.Value, answered.Warning));

    private bool Settle<T>(AdminAnswer<T> answer, Func<AdminAnswer<T>.Answered, IAction> outcome)
    {
        switch (answer)
        {
            case AdminAnswer<T>.Answered answered:
                _dispatcher.Dispatch(outcome(answered));
                return true;
            case AdminAnswer<T>.SignedOut:
                _accounts.SendToSignIn();
                break;
            case AdminAnswer<T>.Refused refused:
                _dispatcher.Dispatch(new HouseholdServersNotSaved(
                    new AdminNotice(AdminNoticeKind.Refused, refused.Refusal.Message, refused.Refusal.Problems)));
                break;
            case AdminAnswer<T>.NotAllowed notAllowed:
                _dispatcher.Dispatch(new HouseholdServersNotSaved(new AdminNotice(AdminNoticeKind.Unavailable, notAllowed.Message)));
                break;
            default:
                _dispatcher.Dispatch(new HouseholdServersNotSaved(
                    new AdminNotice(AdminNoticeKind.Unavailable, AdminNotice.UnreachableMessage)));
                break;
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
    }
}