using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;

namespace Tests.Unit.McpChannelSignalR.Fixtures;

// The accounts service's spaces view as the hub reads it: the proof must be the accounts service's
// assertion of somebody on the chat hub's channel, and the answer is the spaces of the household that
// assertion names — or the one default space of a household that saved none — refused when the hub
// expected another household, as the real client refuses an answer it did not ask for.
public sealed class HouseholdSpacesAccounts : IHouseholdConfiguration
{
    public Dictionary<string, SpacesSection> Saved { get; } = [];

    public async Task<SpacesSection> SpacesAsync(string householdId, HouseholdProof proof, CancellationToken ct)
    {
        var person = await TestAssertions.Verifier().VerifyAsync(proof.Assertion, ChannelIds.SignalR, ct)
                     ?? throw Refused("the proof is no assertion of anybody on signalr");
        return person.TenantId == householdId
            ? Saved.GetValueOrDefault(householdId) ?? new SpacesSection()
            : throw Refused($"the proof names household {person.TenantId}, not {householdId}");
    }

    public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) =>
        throw new NotSupportedException("the hub reads the spaces view alone");

    public Task<HouseholdSettings> SettingsAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException("the hub reads the spaces view alone");

    public Task<IdealistaKeys?> IdealistaKeysAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException("the hub reads the spaces view alone");

    public Task<IReadOnlyList<HouseholdTelegramBot>> TelegramBotsAsync(CancellationToken ct) =>
        throw new NotSupportedException("the hub reads the spaces view alone");

    public Task<HouseholdAgentNames> AgentNamesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException("the hub reads the spaces view alone");

    private static HouseholdConfigRefusedException Refused(string detail) =>
        new(new HouseholdConfigRefused(HouseholdConfigRefusal.Unproven, detail));
}