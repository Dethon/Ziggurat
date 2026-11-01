using Domain.DTOs.Household;

namespace Domain.Contracts;

// The household a call, a request or a fire acts for — as the server it reached verified it — and
// that household's configuration as this server may read it. Built in one place
// (Mcp.Hosting/CallHousehold.cs), from the proof the server holds and never from a household it
// picks; the accounts service believes only the proof, and the reader keeps what it is answered
// briefly, dropping it the moment a save is announced. Plain data a server hands down to whatever
// needs it (a mount through FileSystemCaller), never an ambient.
public interface ICallHousehold
{
    // The household the proof names, as the server already verified it (the call token's `tid`).
    string Id { get; }

    Task<HouseholdSettings> SettingsAsync(CancellationToken ct);

    // None when the household has set no Idealista keys.
    Task<IdealistaKeys?> IdealistaKeysAsync(CancellationToken ct);

    // The agents the household runs, under their names: what the schedules mount shows it.
    Task<HouseholdAgentNames> AgentNamesAsync(CancellationToken ct);
}

public static class CallHouseholds
{
    // The zone the household reads its dates in. A call acting for no household, or a household whose
    // zone this host does not know (its time zone database is not the accounts service's), reads them
    // in the server's own zone — the deployment's, which is also a household's default.
    public static async Task<TimeZoneInfo> ZoneAsync(this ICallHousehold? household, TimeZoneInfo fallback, CancellationToken ct) =>
        household is null
            ? fallback
            : TimeZoneInfo.TryFindSystemTimeZoneById((await household.SettingsAsync(ct)).TimeZone, out var zone)
                ? zone
                : fallback;
}