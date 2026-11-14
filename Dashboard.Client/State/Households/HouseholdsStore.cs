using Domain.DTOs.Admin;

namespace Dashboard.Client.State.Households;

public record SetHouseholds(IReadOnlyList<HouseholdListEntry> Households) : IAction;
public record SetHouseholdsBusy(bool Busy) : IAction;
public record SetHouseholdsNotice(HouseholdsNotice? Notice) : IAction;
public record ShowOwnerLink(OwnerLink Link) : IAction;
public record ForgetOwnerLink : IAction;

public sealed class HouseholdsStore : Store<HouseholdsState>
{
    public HouseholdsStore() : base(new HouseholdsState()) { }

    public void SetHouseholds(IReadOnlyList<HouseholdListEntry> households) =>
        Dispatch(new SetHouseholds(households), static (s, a) => s with { Households = a.Households, Loaded = true });

    public void SetBusy(bool busy) =>
        Dispatch(new SetHouseholdsBusy(busy), static (s, a) => s with { Busy = a.Busy });

    public void SetNotice(HouseholdsNotice? notice) =>
        Dispatch(new SetHouseholdsNotice(notice), static (s, a) => s with { Notice = a.Notice });

    public void ShowLink(OwnerLink link) =>
        Dispatch(new ShowOwnerLink(link), static (s, a) => s with { ShownLink = a.Link, Notice = null });

    public void ForgetLink() =>
        Dispatch(new ForgetOwnerLink(), static (s, _) => s with { ShownLink = null });
}