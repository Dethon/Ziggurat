using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Infrastructure.Agents;

// A person's standing from their household's agent view, which the agent reads for every turn anyway:
// kept by the reader for a minute, and dropped the moment the accounts service announces the
// household's people changed, so a member disabled is refused from their next message on.
public sealed class HouseholdAccountStanding(IHouseholdConfiguration configuration) : IAccountStanding
{
    public async Task<AccountStanding> StandingAsync(Account account, CancellationToken ct) =>
        (await configuration.ForAgentAsync(account.TenantId, ct)).DisabledPeople?.Contains(account.UserId) == true
            ? AccountStanding.Disabled
            : AccountStanding.Active;
}