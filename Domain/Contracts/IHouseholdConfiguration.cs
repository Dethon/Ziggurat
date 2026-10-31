using Domain.DTOs.Household;

namespace Domain.Contracts;

// A household's configuration as each reader may see it, asked of the accounts service and kept
// briefly, dropped the moment a save of that household is announced (.claude/rules/household-config.md).
// Each reader asks for its own view alone: the service answers a view only to the callers it is for.
//
// A shared server names the household it expects — the one the call it is serving verified as — and
// hands over the proof it holds of it; the service believes only the proof, and a household it names
// that is not the expected one is refused. What is cached is keyed by the expected household, which
// the server has already verified itself.
//
// Throws HouseholdConfigRefusedException for an answer the service refused, and an HttpRequestException
// for one it could not give, so a reader never mistakes an outage for a household with nothing set.
public interface IHouseholdConfiguration
{
    // The agent's view, named by id: for a turn, the household of the identity it verified; for work
    // with no call behind it (memory extraction, dreaming), the household the work carries.
    Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct);

    Task<HouseholdSettings> SettingsAsync(string householdId, HouseholdProof proof, CancellationToken ct);

    // None when the household has set no Idealista keys.
    Task<IdealistaKeys?> IdealistaKeysAsync(string householdId, HouseholdProof proof, CancellationToken ct);

    Task<SpacesSection> SpacesAsync(string householdId, HouseholdProof proof, CancellationToken ct);

    // The household's agents by id and name, and its default: what a person picks among on a channel.
    Task<HouseholdAgentNames> AgentNamesAsync(string householdId, HouseholdProof proof, CancellationToken ct);

    // Every household's bots: the Telegram server polls them from startup, before any call.
    Task<IReadOnlyList<HouseholdTelegramBot>> TelegramBotsAsync(CancellationToken ct);
}

// What a shared server holds that names a household: the agent's call token it was called with, the
// accounts service's assertion of the person it serves on its own channel, or — for a schedule's fire,
// which no call raised — the delegation the agent signed for the schedule's last write. Exactly one.
public sealed record HouseholdProof
{
    private HouseholdProof(string? callToken, string? assertion, string? delegation)
    {
        CallToken = callToken;
        Assertion = assertion;
        Delegation = delegation;
    }

    public string? CallToken { get; }
    public string? Assertion { get; }
    public string? Delegation { get; }

    public static HouseholdProof OfCallToken(string callToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callToken);
        return new HouseholdProof(callToken, null, null);
    }

    public static HouseholdProof OfAssertion(string assertion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assertion);
        return new HouseholdProof(null, assertion, null);
    }

    public static HouseholdProof OfDelegation(string delegation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(delegation);
        return new HouseholdProof(null, null, delegation);
    }
}

public sealed class HouseholdConfigRefusedException(HouseholdConfigRefused refusal)
    : Exception($"The accounts service refused the household configuration ({refusal.Reason}): {refusal.Detail}")
{
    public HouseholdConfigRefused Refusal { get; } = refusal;
}