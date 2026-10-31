using System.Text.Json;
using System.Text.Json.Nodes;
using Accounts.Announcements;
using Accounts.Store;
using Domain.DTOs.Household;
using Domain.Security.Sealing;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Configuration;

public sealed record ConfiguredHousehold(string Id, string Name, bool IsHome);

public abstract record SectionSave
{
    // NotTold is why the readers were not told, or null when they were: the save stands either way,
    // and a reader sees it once what it cached runs out.
    public sealed record Saved(int SchemaVersion, string? NotTold) : SectionSave;

    public sealed record NoHousehold : SectionSave;

    public sealed record Invalid(IReadOnlyList<string> Problems) : SectionSave;
}

// A household's configuration, section by section (.claude/rules/household-config.md). Every read
// and every save carries a section to its current shape: the stored document's sealed fields are
// opened, the plain document migrated forward and written out whole, so a reader never sees an old
// shape or a field left to chance. A save seals the schema's secrets again for their new places,
// under the household's data key, and announces itself so every reader drops what it cached.
public sealed class HouseholdConfigStore(
    IDbContextFactory<AccountsDbContext> contexts, HouseholdDataKeys dataKeys, AccountsClock clock, RedisAnnouncer announcer)
{
    public async Task<ConfiguredHousehold?> HouseholdAsync(string householdId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Tenants.AsNoTracking()
            .Where(t => t.Id == householdId)
            .Select(t => new ConfiguredHousehold(t.Id, t.Name, t.IsHome))
            .SingleOrDefaultAsync(ct);
    }

    // The households that have saved a section, for a view across households.
    public async Task<IReadOnlyList<string>> HouseholdsWithAsync(string section, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.HouseholdSections.AsNoTracking().Where(s => s.Section == section).Select(s => s.TenantId).ToListAsync(ct);
    }

    // The section's plain document at its current shape, or null for a household that does not exist.
    public async Task<JsonObject?> ReadAsync(string householdId, SectionSchema schema, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == householdId, ct) is not { } household)
        {
            return null;
        }

        return await PlainAsync(db, household, schema, ct);
    }

    public async Task<T?> ReadAsync<T>(string householdId, SectionSchema<T> schema, CancellationToken ct) where T : class =>
        await ReadAsync(householdId, (SectionSchema)schema, ct) is { } document ? schema.Read(document) : null;

    public async Task<SectionSave> SaveAsync(string householdId, SectionSchema schema, JsonObject input, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Tenants.SingleOrDefaultAsync(t => t.Id == householdId, ct) is not { } household)
        {
            return new SectionSave.NoHousehold();
        }

        JsonObject migrated;
        try
        {
            migrated = schema.Migrate(input);
        }
        catch (SectionShapeException ex)
        {
            return new SectionSave.Invalid([ex.Message]);
        }

        var (kept, unkept) = SealedFields.Keep(migrated, SealedFields.HasMarkers(migrated)
            ? await PlainAsync(db, household, schema, ct)
            : null);
        var problems = unkept.Count > 0 ? unkept : schema.Problems(kept);
        if (problems.Count > 0)
        {
            return new SectionSave.Invalid(problems);
        }

        var plain = schema.Normalize(kept);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.Time.GetUtcNow();
        var document = SealedFields.HasSecret(plain, schema.SecretPaths)
            ? SealedFields.Seal(plain, schema.SecretPaths, await DataKeyAsync(db, household.Id, now, ct), household.Id, schema.Name)
            : plain;
        var row = await db.HouseholdSections.SingleOrDefaultAsync(s => s.TenantId == household.Id && s.Section == schema.Name, ct);
        if (row is null)
        {
            db.HouseholdSections.Add(new HouseholdSection
            {
                Id = Tokens.NewId(), TenantId = household.Id, Section = schema.Name,
                Document = document.ToJsonString(), UpdatedAt = now
            });
        }
        else
        {
            row.Document = document.ToJsonString();
            row.UpdatedAt = now;
        }

        // The household's name is its settings', and the tenant row carries it for the lists that
        // name households.
        if (schema == HouseholdSections.SettingsSchema)
        {
            household.Name = HouseholdSections.SettingsSchema.Read(plain).Name;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var notTold = await announcer.AnnounceAsync(
            HouseholdConfigProtocol.SavedChannel, new HouseholdConfigSaved(household.Id, schema.Name), ct);
        return new SectionSave.Saved(schema.CurrentVersion, notTold);
    }

    // Every household's data key wrapped under this service's master instead of the previous one, in
    // one transaction, then every sealed field of every household opened to prove it: what failed, or
    // how many households' keys were rewrapped and how many secrets opened.
    public async Task<(IReadOnlyList<string> Failures, int Rewrapped, int Opened)> RewrapAsync(
        SealingKey previous, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.Time.GetUtcNow();
        var rows = await db.HouseholdDataKeys.ToListAsync(ct);
        var before = rows.ToDictionary(r => r.Id, r => r.MasterKeyId);
        var failures = rows.Select(row => dataKeys.Rewrap(row, previous, now)).OfType<string>().ToList();
        if (failures.Count > 0)
        {
            return (failures, 0, 0);
        }

        await db.SaveChangesAsync(ct);
        var rewrapped = rows.Count(r => before[r.Id] != r.MasterKeyId);

        var keys = rows.ToDictionary(r => r.TenantId, dataKeys.Open);
        var sections = await db.HouseholdSections.AsNoTracking().ToListAsync(ct);
        var opened = sections.Select(section =>
        {
            var stored = JsonNode.Parse(section.Document)!.AsObject();
            try
            {
                SealedFields.Open(stored, keys.GetValueOrDefault(section.TenantId), section.TenantId, section.Section);
                return (Opened: SealedFields.Count(stored), Failure: (string?)null);
            }
            catch (HouseholdSecretsUnavailableException ex)
            {
                return (Opened: 0, Failure: ex.Message);
            }
        }).ToList();

        return ([.. opened.Select(o => o.Failure).OfType<string>()], rewrapped, opened.Sum(o => o.Opened));
    }

    private async Task<JsonObject> PlainAsync(AccountsDbContext db, Tenant household, SectionSchema schema, CancellationToken ct)
    {
        var row = await db.HouseholdSections.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == household.Id && s.Section == schema.Name, ct);
        if (row is null)
        {
            return Default(household, schema);
        }

        JsonObject stored;
        try
        {
            stored = JsonNode.Parse(row.Document)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new SectionShapeException($"household {household.Id}'s {schema.Name} section is not JSON: {ex.Message}");
        }

        var dataKey = SealedFields.AnySealed(stored)
            && await db.HouseholdDataKeys.AsNoTracking().SingleOrDefaultAsync(k => k.TenantId == household.Id, ct) is { } key
                ? dataKeys.Open(key)
                : null;
        return schema.Normalize(schema.Migrate(SealedFields.Open(stored, dataKey, household.Id, schema.Name)));
    }

    // A household that saved no settings is named as its row is and reads dates in the deployment's
    // zone, which is the one the home household's schedules always ran in.
    private static JsonObject Default(Tenant household, SectionSchema schema) =>
        schema == HouseholdSections.SettingsSchema
            ? HouseholdSections.SettingsSchema.Write(new HouseholdSettings
            {
                Name = household.Name, TimeZone = TimeZoneInfo.Local.Id
            })
            : schema.Default;

    private async Task<SealingKey> DataKeyAsync(AccountsDbContext db, string householdId, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.HouseholdDataKeys.SingleOrDefaultAsync(k => k.TenantId == householdId, ct) is { } row)
        {
            return dataKeys.Open(row);
        }

        var (minted, key) = dataKeys.Mint(householdId, now);
        db.HouseholdDataKeys.Add(minted);
        return key;
    }
}