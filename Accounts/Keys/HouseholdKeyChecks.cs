using Accounts.Store;
using Domain.DTOs.Admin;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Keys;

// What each provider last said of each of a household's keys (HouseholdKeyCheck), by the key's name
// in the keys section and its fingerprint. Never the key: the fingerprint is a SHA-256 of it, which a
// read compares with the key saved now, so a key changed since it was checked reads as unchecked.
public sealed class HouseholdKeyChecks(IDbContextFactory<AccountsDbContext> contexts, AccountsClock clock)
{
    public const int KeyNameLength = 100;
    public const int ReasonLength = 300;

    public async Task<IReadOnlyDictionary<string, HouseholdKeyCheck>> ForAsync(string householdId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.HouseholdKeyChecks.AsNoTracking()
            .Where(c => c.TenantId == householdId)
            .ToDictionaryAsync(c => c.Key, StringComparer.Ordinal, ct);
    }

    public async Task RecordAsync(
        string householdId, string keyName, string material, KeyState state, string? reason, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.HouseholdKeyChecks.SingleOrDefaultAsync(c => c.TenantId == householdId && c.Key == keyName, ct);
        if (row is null)
        {
            row = new HouseholdKeyCheck
            {
                Id = Tokens.NewId(), TenantId = householdId, Key = keyName, Fingerprint = "", State = ""
            };
            db.HouseholdKeyChecks.Add(row);
        }

        row.Fingerprint = Fingerprint(material);
        row.State = state.ToString();
        row.CheckedAt = clock.Time.GetUtcNow();
        row.Reason = reason is null ? null : reason.Length <= ReasonLength ? reason : reason[..ReasonLength];
        await db.SaveChangesAsync(ct);
    }

    public static string Fingerprint(string material) => Tokens.Hash(material);
}