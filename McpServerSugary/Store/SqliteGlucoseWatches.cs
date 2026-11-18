using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.Store;

// The household's glucose watches, a context per call opened for the account asking: its household
// filter keeps every read and write to that household, and who of it is shown a watch is said here, in
// the one list that answers it — a watch is no person's row, so no filter can.
public sealed class SqliteGlucoseWatches(SugaryStore store) : IGlucoseWatches
{
    public async Task<IReadOnlyList<StoredGlucoseWatch>> ListAsync(Account reader, bool everyone, CancellationToken ct)
    {
        await using var db = store.For(reader);
        var rows = await db.GlucoseWatches.AsNoTracking()
            .Where(w => everyone || w.SubjectUserId == reader.UserId || w.WriterUserId == reader.UserId)
            .OrderBy(w => w.Id)
            .ToListAsync(ct);
        return rows.Select(ToStored).ToList();
    }

    public async Task<StoredGlucoseWatch?> FindAsync(Account reader, string id, CancellationToken ct)
    {
        await using var db = store.For(reader);
        var row = await db.GlucoseWatches.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        return row is null ? null : ToStored(row);
    }

    public async Task<bool> AddAsync(Account writer, StoredGlucoseWatch watch, CancellationToken ct)
    {
        await using var db = store.For(writer);
        if (await db.GlucoseWatches.AnyAsync(w => w.Id == watch.Id, ct))
        {
            return false;
        }

        db.GlucoseWatches.Add(Fill(new GlucoseWatch { TenantId = writer.TenantId, Id = watch.Id }, watch));
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Another write took the id between the look and the insert.
            return false;
        }
    }

    public async Task<bool> ReplaceAsync(Account writer, StoredGlucoseWatch watch, CancellationToken ct)
    {
        await using var db = store.For(writer);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.GlucoseWatches.FirstOrDefaultAsync(w => w.Id == watch.Id, ct) is not { } row)
        {
            return false;
        }

        Fill(row, watch);
        await db.GlucoseWatchStates.Where(s => s.WatchId == watch.Id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RemoveAsync(Account reader, string id, CancellationToken ct)
    {
        await using var db = store.For(reader);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.GlucoseWatchStates.Where(s => s.WatchId == id).ExecuteDeleteAsync(ct);
        var removed = await db.GlucoseWatches.Where(w => w.Id == id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return removed > 0;
    }

    public async Task<IReadOnlyList<GlucoseTriggerCondition>> ConditionsAsync(Account reader, string id, CancellationToken ct)
    {
        await using var db = store.For(reader);
        var rows = await db.GlucoseWatchStates.AsNoTracking()
            .Where(s => s.WatchId == id)
            .OrderBy(s => s.TriggerIndex)
            .ToListAsync(ct);
        return rows.Select(s => new GlucoseTriggerCondition(s.TriggerIndex, s.Holding, s.HoldingSince, s.FiredThisEpisode)).ToList();
    }

    private static GlucoseWatch Fill(GlucoseWatch row, StoredGlucoseWatch watch)
    {
        row.SubjectUserId = watch.SubjectUserId;
        row.WriterUserId = watch.WriterUserId;
        row.AgentId = watch.AgentId;
        row.Spec = watch.Spec;
        row.Enabled = watch.Enabled;
        row.Once = watch.Once;
        row.DeliverTo = watch.DeliverTo is null ? null : JsonSerializer.Serialize(watch.DeliverTo);
        row.UserId = watch.UserId;
        row.Delegation = watch.Delegation;
        row.CreatedAt = watch.CreatedAt;
        row.LastFiredAt = watch.LastFiredAt;
        row.Spent = watch.Spent;
        return row;
    }

    internal static StoredGlucoseWatch ToStored(GlucoseWatch row) => new()
    {
        Id = row.Id,
        SubjectUserId = row.SubjectUserId,
        WriterUserId = row.WriterUserId,
        AgentId = row.AgentId,
        Spec = row.Spec,
        Enabled = row.Enabled,
        Once = row.Once,
        DeliverTo = row.DeliverTo is null ? null : JsonSerializer.Deserialize<List<string>>(row.DeliverTo),
        UserId = row.UserId,
        Delegation = row.Delegation,
        CreatedAt = row.CreatedAt,
        LastFiredAt = row.LastFiredAt,
        Spent = row.Spent
    };
}