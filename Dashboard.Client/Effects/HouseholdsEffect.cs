using Dashboard.Client.Services;
using Dashboard.Client.State.Households;
using Domain.DTOs.Admin;

namespace Dashboard.Client.Effects;

// The household list's requests and what each answer does to the page: a list replaces the list, a
// minted link is shown once and the list is read again, anything else is said as a notice.
public sealed class HouseholdsEffect(HouseholdsAdminService admin, HouseholdsStore store)
{
    public async Task LoadAsync()
    {
        switch (await admin.ListAsync())
        {
            case HouseholdsAnswer<IReadOnlyList<HouseholdListEntry>>.Answered answered:
                store.SetHouseholds(answered.Value);
                break;
            case var other:
                store.SetNotice(NoticeOf(other));
                break;
        }
    }

    public Task CreateAsync(string name, string ownerName) =>
        MintAsync(() => admin.CreateAsync(new NewHousehold(name, ownerName)));

    public Task MintOwnerRecoveryAsync(string householdId, string userId) =>
        MintAsync(() => admin.MintOwnerRecoveryAsync(householdId, userId));

    private async Task MintAsync(Func<Task<HouseholdsAnswer<OwnerLink>>> mint)
    {
        store.SetBusy(true);
        try
        {
            switch (await mint())
            {
                case HouseholdsAnswer<OwnerLink>.Answered answered:
                    store.ShowLink(answered.Value);
                    await LoadAsync();
                    break;
                case var other:
                    store.SetNotice(NoticeOf(other));
                    break;
            }
        }
        finally
        {
            store.SetBusy(false);
        }
    }

    private static HouseholdsNotice NoticeOf<T>(HouseholdsAnswer<T> answer) => answer switch
    {
        HouseholdsAnswer<T>.Refused refused => new HouseholdsNotice(refused.Refusal.Message, refused.Refusal.Problems),
        HouseholdsAnswer<T>.NotAllowed notAllowed => new HouseholdsNotice(notAllowed.Message, []),
        HouseholdsAnswer<T>.SignedOut => new HouseholdsNotice("You are signed out: reload the page to sign in again.", []),
        _ => new HouseholdsNotice("The accounts service could not be reached. Try again in a moment.", [])
    };
}