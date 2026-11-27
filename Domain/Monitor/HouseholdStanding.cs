using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security.Signing;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

// Whether a household lets the agent act for whoever something is for: the one rule a person's turn, a
// fire (ConversationGroup) and a built app's read (AppReader) are held to. An assertion or a delegation
// says who it is for as of when it was signed — an assertion Telegram kept for minutes, a delegation as
// old as its schedule, an app session's held for five — so the households it names are asked, every
// time rather than once (a group outlives its turns, so a household suspended mid-conversation has its
// next message refused), whether they run, and the person's whether it still lets the agent act for
// them. The guest and nobody are nobody an owner can disable. Each answer is the reader's, kept a minute
// and dropped the moment accounts announces a change. One that cannot be read refuses as unchecked —
// never as suspended or disabled, which it may not be, and never let through.
//
// Optional parts as a test harness has them: no households' provider suspends nobody, no standing
// disables nobody.
public sealed class HouseholdStanding(IAgentDefinitionProvider? households, IAccountStanding? standing, ILogger logger)
{
    // Null when it may go on.
    public async Task<StandingRefusal?> RefusalAsync(
        IReadOnlyList<string?> householdIds, IdentityClaims? person, CancellationToken ct)
    {
        foreach (var householdId in householdIds.OfType<string>().Where(h => h.Length > 0).Distinct())
        {
            if (await SuspensionRefusalAsync(householdId, ct) is { } refusal)
            {
                return refusal;
            }
        }

        return person?.Account is { } account ? await DisablingRefusalAsync(account, ct) : null;
    }

    private async Task<StandingRefusal?> SuspensionRefusalAsync(string householdId, CancellationToken ct)
    {
        if (households is null)
        {
            return null;
        }

        try
        {
            return (await households.ForHouseholdAsync(householdId, ct)).Suspended
                ? new StandingRefusal.Suspended(householdId)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read whether household {Household} is suspended", householdId);
            return new StandingRefusal.Unchecked(householdId);
        }
    }

    private async Task<StandingRefusal?> DisablingRefusalAsync(Account account, CancellationToken ct)
    {
        if (standing is null)
        {
            return null;
        }

        try
        {
            return await standing.StandingAsync(account, ct) == AccountStanding.Disabled
                ? new StandingRefusal.Disabled(account)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read whether account {UserId} of household {Household} is enabled",
                account.UserId, account.TenantId);
            return new StandingRefusal.Unchecked(account.TenantId);
        }
    }
}

// Why a household would not have the agent act for somebody who was believed: the operator suspended it,
// an owner disabled the person, it turned off the feature the act needs, it no longer has the agent a built
// app asks, or none of it could be read.
public abstract record StandingRefusal(string HouseholdId)
{
    public sealed record Suspended(string HouseholdId) : StandingRefusal(HouseholdId);

    public sealed record Disabled(Account Account) : StandingRefusal(Account.TenantId);

    public sealed record Unchecked(string HouseholdId) : StandingRefusal(HouseholdId);

    public sealed record SugaryOff(string HouseholdId) : StandingRefusal(HouseholdId);

    public sealed record AppsOff(string HouseholdId) : StandingRefusal(HouseholdId);

    public sealed record AppAgentGone(string HouseholdId, string AgentId) : StandingRefusal(HouseholdId);
}