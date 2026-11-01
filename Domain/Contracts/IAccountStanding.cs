using Domain.DTOs.Channel;

namespace Domain.Contracts;

// Whether the agent may act for a person now: the accounts service's word, read from the person's
// household's configuration view (HouseholdAccountStanding) as the turn or fire is admitted. An
// assertion or a delegation says who a turn is for; this says whether that person is still somebody
// the household lets the agent act for — a member an owner disabled is not, whatever was signed
// before. Throws what reading the view threw: a person whose standing cannot be read is never taken
// for one in good standing.
public interface IAccountStanding
{
    Task<AccountStanding> StandingAsync(Account account, CancellationToken ct);
}

public enum AccountStanding
{
    Active,

    // An owner disabled the member: their turns are refused and their fires act for nobody. Their data
    // is kept, and re-enabling them brings it all back.
    Disabled
}