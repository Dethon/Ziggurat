using Domain.DTOs.Admin;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Admin;

namespace WebChat.Client.State.Effects;

// The admin area's members page asks the admin API through here: the household's people loaded when
// the page opens, each change answered with the people as they are now, and an invite or a recovery
// link answered with the link, which the page shows once. A page signed out since it loaded is sent to
// sign in and back, as the chat is.
public sealed class HouseholdMembersEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IAdminService _admin;
    private readonly IAccountService _accounts;
    private readonly IReadOnlyList<IDisposable> _registrations;

    public HouseholdMembersEffect(
        Dispatcher dispatcher, IAdminService admin, IAccountService accounts, ILogger<HouseholdMembersEffect> logger)
    {
        _dispatcher = dispatcher;
        _admin = admin;
        _accounts = accounts;

        _registrations =
        [
            dispatcher.RegisterHandler<LoadHouseholdMembers>(_ =>
                LoadAsync().LogFaults(logger, nameof(LoadHouseholdMembers))),
            dispatcher.RegisterHandler<InviteHouseholdMember>(action =>
                MintAsync(_admin.InviteMemberAsync(action.Invite), $"An invite for {action.Invite.DisplayName}")
                    .LogFaults(logger, nameof(InviteHouseholdMember))),
            dispatcher.RegisterHandler<SaveHouseholdMember>(action =>
                ChangeAsync(_admin.SaveMemberAsync(action.UserId, action.Edit)).LogFaults(logger, nameof(SaveHouseholdMember))),
            dispatcher.RegisterHandler<DisableHouseholdMember>(action =>
                ChangeAsync(_admin.DisableMemberAsync(action.UserId)).LogFaults(logger, nameof(DisableHouseholdMember))),
            dispatcher.RegisterHandler<EnableHouseholdMember>(action =>
                ChangeAsync(_admin.EnableMemberAsync(action.UserId)).LogFaults(logger, nameof(EnableHouseholdMember))),
            dispatcher.RegisterHandler<MintMemberRecovery>(action =>
                MintAsync(_admin.MintMemberRecoveryAsync(action.UserId), $"A recovery link for {action.DisplayName}")
                    .LogFaults(logger, nameof(MintMemberRecovery))),
            dispatcher.RegisterHandler<MintTelegramGroupCode>(_ =>
                MintGroupCodeAsync().LogFaults(logger, nameof(MintTelegramGroupCode)))
        ];
    }

    public async Task LoadAsync() =>
        Settle(await _admin.GetMembersAsync(), answered => new HouseholdMembersLoaded(answered.Value));

    private async Task ChangeAsync(Task<AdminAnswer<AdminMembers>> change) =>
        Settle(await change, answered => new HouseholdMembersSaved(answered.Value, answered.Warning));

    private async Task MintAsync(Task<AdminAnswer<MemberLink>> mint, string what) =>
        Settle(await mint, answered => new MemberLinkMinted(what, answered.Value));

    private async Task MintGroupCodeAsync() =>
        Settle(await _admin.MintTelegramGroupCodeAsync(), answered => new TelegramGroupCodeMinted(answered.Value));

    private void Settle<T>(AdminAnswer<T> answer, Func<AdminAnswer<T>.Answered, IAction> outcome) =>
        _dispatcher.Settle(_accounts, answer, outcome, notice => new HouseholdMembersNotSaved(notice));

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
    }
}