using Domain.Contracts;
using Domain.DTOs.Household;
using Infrastructure.Clients.Accounts;

namespace Tests.Integration.Fixtures;

// An apps server's household configuration with its spaces and app asks as a real accounts service answers
// them, on the viewer's own proof; the rest as the fixture's stand-in answers it, since a call token the test
// signs names a server the accounts service does not know (the stand-in's agent names are
// AppsServer.HouseholdAgents).
public sealed class HouseholdConfigFromAccounts(HouseholdConfigurationClient accounts) : IHouseholdConfiguration
{
    private readonly EveryHouseholdIn _standIn = new(TimeZoneInfo.Utc, AppsServer.HouseholdAgents);

    public Task<SpacesSection> SpacesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        accounts.SpacesAsync(householdId, proof, ct);

    public Task<AppAsksSection> AppAsksAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        accounts.AppAsksAsync(householdId, proof, ct);

    public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) => _standIn.ForAgentAsync(householdId, ct);

    public Task<HouseholdSettings> SettingsAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        _standIn.SettingsAsync(householdId, proof, ct);

    public Task<IdealistaKeys?> IdealistaKeysAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        _standIn.IdealistaKeysAsync(householdId, proof, ct);

    public Task<HouseholdAgentNames> AgentNamesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        _standIn.AgentNamesAsync(householdId, proof, ct);

    public Task<IReadOnlyList<HouseholdTelegramBot>> TelegramBotsAsync(CancellationToken ct) => _standIn.TelegramBotsAsync(ct);

    public Task<IReadOnlyList<HouseholdMembers>> PeopleAsync(CancellationToken ct) => _standIn.PeopleAsync(ct);
}