using Domain.Contracts;
using Domain.DTOs.Household;

namespace Tests;

// The household configuration a shared server reads, for a test or an eval stack with no accounts
// service: every household is set in one zone (but those `zones` names) and has set no Idealista keys, whatever proof is shown.
// What a server makes of a real household's section, asked with what its call proved, is
// CallHouseholdTests' (seam 4); a stack that only needs the zone its dates are read in takes this.
// `agents` are every household's agents, for a stack whose schedules mount lists them.
internal sealed class EveryHouseholdIn(
    TimeZoneInfo zone, IReadOnlyList<HouseholdAgentName>? agents = null,
    IReadOnlyDictionary<string, TimeZoneInfo>? zones = null) : IHouseholdConfiguration
{
    public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) =>
        throw new NotSupportedException("a shared server's stand-in answers no agent");

    public Task<HouseholdSettings> SettingsAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        Task.FromResult(new HouseholdSettings { Name = "Casa", TimeZone = (zones?.GetValueOrDefault(householdId) ?? zone).Id });

    public Task<IdealistaKeys?> IdealistaKeysAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        Task.FromResult<IdealistaKeys?>(null);

    public Task<SpacesSection> SpacesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        Task.FromResult(new SpacesSection());

    public Task<IReadOnlyList<HouseholdTelegramBot>> TelegramBotsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HouseholdTelegramBot>>([]);

    public Task<HouseholdAgentNames> AgentNamesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        agents is null
            ? throw new NotSupportedException("a shared server's stand-in lists no agents")
            : Task.FromResult(new HouseholdAgentNames(agents.FirstOrDefault()?.Id, agents));

    public Task<IReadOnlyList<HouseholdMembers>> PeopleAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HouseholdMembers>>([]);
}