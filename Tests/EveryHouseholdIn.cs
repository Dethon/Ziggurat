using Domain.Contracts;
using Domain.DTOs.Household;

namespace Tests;

// The household configuration a shared server reads, for a test or an eval stack with no accounts
// service: every household is set in one zone and has set no Idealista keys, whatever proof is shown.
// What a server makes of a real household's section, asked with what its call proved, is
// CallHouseholdTests' (seam 4); a stack that only needs the zone its dates are read in takes this.
internal sealed class EveryHouseholdIn(TimeZoneInfo zone) : IHouseholdConfiguration
{
    public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) =>
        throw new NotSupportedException("a shared server's stand-in answers no agent");

    public Task<HouseholdSettings> SettingsAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        Task.FromResult(new HouseholdSettings { Name = "Casa", TimeZone = zone.Id });

    public Task<IdealistaKeys?> IdealistaKeysAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        Task.FromResult<IdealistaKeys?>(null);

    public Task<SpacesSection> SpacesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        Task.FromResult(new SpacesSection());

    public Task<IReadOnlyList<HouseholdTelegramBot>> TelegramBotsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HouseholdTelegramBot>>([]);

    public Task<HouseholdAgentNames> AgentNamesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException("a shared server's stand-in lists no agents");
}