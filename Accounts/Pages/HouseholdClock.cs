using Accounts.Configuration;
using Domain.DTOs.Household;
using Domain.Extensions;

namespace Accounts.Pages;

// The zone a household's pages say a time in: its settings' own, or this host's where it names none
// this host knows.
public sealed class HouseholdClock(HouseholdConfigStore store)
{
    public async Task<TimeZoneInfo> ZoneAsync(string householdId, CancellationToken ct) =>
        TimeZones.Named((await store.ReadAsync(householdId, HouseholdSections.SettingsSchema, ct))?.TimeZone)
        ?? TimeZoneInfo.Local;
}