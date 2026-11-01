using Domain.DTOs.Admin;

namespace WebChat.Client.State.Admin;

public record LoadHouseholdMembers : IAction;

public record HouseholdMembersLoaded(AdminMembers Members) : IAction;

public record InviteHouseholdMember(MemberInvite Invite) : IAction;

public record SaveHouseholdMember(string UserId, MemberEdit Edit) : IAction;

public record DisableHouseholdMember(string UserId) : IAction;

public record EnableHouseholdMember(string UserId) : IAction;

public record MintMemberRecovery(string UserId, string DisplayName) : IAction;

// A change the admin API answered with the household's people as they are now.
public record HouseholdMembersSaved(AdminMembers Members, string? Warning) : IAction;

// A link the admin API minted, for whom and what it is: shown until the page is left, never again.
public record MemberLinkMinted(string What, MemberLink Link) : IAction;

// A load or a change the admin API did not answer: refused, not allowed, unreachable.
public record HouseholdMembersNotSaved(AdminNotice Notice) : IAction;

// A link the page shows once: what it is ("An invite for Laura") and the link itself.
public sealed record MintedMemberLink(string What, MemberLink Link);

public sealed record HouseholdMembersState
{
    // Null until the admin API has answered with them.
    public AdminMembers? Members { get; init; }

    public MintedMemberLink? Minted { get; init; }

    public bool Saving { get; init; }

    public AdminNotice? Notice { get; init; }

    public static HouseholdMembersState Initial => new();
}

// The household's people as the admin area's members page shows them: only ever what the admin API
// answered. A minted link is kept here alone — in memory, for this page — since the API never shows it
// again; a later change clears it.
public sealed class HouseholdMembersStore : IDisposable
{
    private readonly Store<HouseholdMembersState> _store;

    public HouseholdMembersStore(Dispatcher dispatcher)
    {
        _store = new Store<HouseholdMembersState>(HouseholdMembersState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public HouseholdMembersState State => _store.State;

    public IObservable<HouseholdMembersState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static HouseholdMembersState Reduce(HouseholdMembersState state, IAction action) => action switch
    {
        LoadHouseholdMembers => state with { Notice = null },
        HouseholdMembersLoaded a => state with { Members = a.Members },
        InviteHouseholdMember or SaveHouseholdMember or DisableHouseholdMember or EnableHouseholdMember or MintMemberRecovery =>
            state with { Saving = true, Notice = null, Minted = null },
        HouseholdMembersSaved a => state with
        {
            Members = a.Members,
            Saving = false,
            Notice = a.Warning is { } warning
                ? new AdminNotice(AdminNoticeKind.Warning, warning)
                : new AdminNotice(AdminNoticeKind.Saved, "Saved.")
        },
        MemberLinkMinted a => state with { Saving = false, Minted = new MintedMemberLink(a.What, a.Link) },
        HouseholdMembersNotSaved a => state with { Saving = false, Notice = a.Notice },
        _ => state
    };
}