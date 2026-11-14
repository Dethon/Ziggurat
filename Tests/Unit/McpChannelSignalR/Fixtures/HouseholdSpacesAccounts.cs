using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;

namespace Tests.Unit.McpChannelSignalR.Fixtures;

// The accounts service's spaces and agent-names views as the hub reads them: the proof must be the
// accounts service's assertion of somebody on the chat hub's channel, and the answer is the section of
// the household that assertion names — or, for a household that saved none, the one default space and
// the two templates — refused when the hub expected another household, as the real client refuses an
// answer it did not ask for.
public sealed class HouseholdSpacesAccounts : IHouseholdConfiguration
{
    public Dictionary<string, SpacesSection> Saved { get; } = [];

    public Dictionary<string, AgentsSection> SavedAgents { get; } = [];

    public async Task<SpacesSection> SpacesAsync(string householdId, HouseholdProof proof, CancellationToken ct)
    {
        await RequireProvenAsync(householdId, proof, ct);
        return Saved.GetValueOrDefault(householdId) ?? new SpacesSection();
    }

    public async Task<HouseholdAgentNames> AgentNamesAsync(string householdId, HouseholdProof proof, CancellationToken ct)
    {
        await RequireProvenAsync(householdId, proof, ct);
        return HouseholdAgents.Names(SavedAgents.GetValueOrDefault(householdId) ?? new AgentsSection());
    }

    private static async Task RequireProvenAsync(string householdId, HouseholdProof proof, CancellationToken ct)
    {
        var person = await TestAssertions.Verifier().VerifyAsync(proof.Assertion, ChannelIds.SignalR, ct)
                     ?? throw Refused("the proof is no assertion of anybody on signalr");
        if (person.TenantId != householdId)
        {
            throw Refused($"the proof names household {person.TenantId}, not {householdId}");
        }
    }

    public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) =>
        throw new NotSupportedException("the hub reads the spaces and agent-names views alone");

    public Task<HouseholdSettings> SettingsAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException("the hub reads the spaces and agent-names views alone");

    public Task<IdealistaKeys?> IdealistaKeysAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException("the hub reads the spaces and agent-names views alone");

    public Task<IReadOnlyList<HouseholdTelegramBot>> TelegramBotsAsync(CancellationToken ct) =>
        throw new NotSupportedException("the hub reads the spaces and agent-names views alone");

    public Task<IReadOnlyList<HouseholdMembers>> PeopleAsync(CancellationToken ct) =>
        throw new NotSupportedException("the hub reads the spaces and agent-names views alone");

    private static HouseholdConfigRefusedException Refused(string detail) =>
        new(new HouseholdConfigRefused(HouseholdConfigRefusal.Unproven, detail));
}