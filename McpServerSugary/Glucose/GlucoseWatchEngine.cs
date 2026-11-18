using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Domain.Tools.Sugary.Watches;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Glucose;

// Judges a person's glucose watches (GlucoseTriggers): after each poll of her link, every trigger, and on
// the clock's pass (GlucoseWatchLoop), the sensor's triggers alone — a link refused or backing off is
// asked rarely or never, so "failing for an hour" and "the sensor ends tomorrow" cannot wait for a poll.
// What was judged is kept per trigger, so a restart neither fires an episode again nor forgets a `for`.
// A fire runs the watch's effects (IGlucoseWatchEffects), and the states, the fire's time and a `once`
// watch spent are then written together — only while the watch is still the one judged, so a watch
// rewritten or removed meanwhile is never given back what was judged of the old one.
//
// Every watch's subject is somebody's, and the engine wakes for nobody's request, so the factory's
// context is used and every query says IgnoreQueryFilters with the owner spelled out.
public sealed class GlucoseWatchEngine(
    IDbContextFactory<SugaryDbContext> contexts,
    IGlucoseWatchEffects effects,
    HomeCalendar calendar,
    TimeProvider clock,
    ILogger<GlucoseWatchEngine> logger) : IGlucosePollObserver
{
    // One judgement per person at a time: a poll and the clock's pass judging her at once would each read
    // an episode as not fired and fire it twice.
    private readonly ConcurrentDictionary<Account, SemaphoreSlim> _turns = new();

    public Task PolledAsync(GlucosePoll poll, CancellationToken ct) => JudgeAsync(poll.Person, poll, ct);

    // The clock's pass: everyone with an armed watch, one after another, one person's failure touching
    // nobody else's.
    public async Task TickAsync(CancellationToken ct)
    {
        List<Account> subjects;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            subjects = (await db.GlucoseWatches.IgnoreQueryFilters().AsNoTracking()
                    .Where(w => w.Enabled && !w.Spent)
                    .Select(w => new { w.TenantId, w.SubjectUserId })
                    .Distinct()
                    .ToListAsync(ct))
                .Select(s => new Account(s.TenantId, s.SubjectUserId))
                .ToList();
        }

        foreach (var subject in subjects)
        {
            try
            {
                await JudgeAsync(subject, null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Judging the glucose watches of {User} on the clock failed; judged again on the next pass",
                    subject.UserId);
            }
        }
    }

    private async Task JudgeAsync(Account person, GlucosePoll? poll, CancellationToken ct)
    {
        var turn = _turns.GetOrAdd(person, _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            await using var db = await contexts.CreateDbContextAsync(ct);
            var watches = await db.GlucoseWatches.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.TenantId == person.TenantId && w.SubjectUserId == person.UserId && w.Enabled && !w.Spent)
                .OrderBy(w => w.Id)
                .ToListAsync(ct);
            if (watches.Count == 0)
            {
                return;
            }

            var ids = watches.Select(w => w.Id).ToList();
            var states = (await db.GlucoseWatchStates.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.TenantId == person.TenantId && ids.Contains(s.WatchId))
                    .ToListAsync(ct))
                .ToLookup(s => s.WatchId);
            var facts = await FactsAsync(db, person, poll, now, ct);
            // What a fire tells the agent is in the unit she reads, her link's; mg/dL without one.
            var unit = await db.GlucoseLinks.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.UserId == person.UserId && l.TenantId == person.TenantId)
                .Select(l => (GlucoseUnit?)l.Unit)
                .FirstOrDefaultAsync(ct) ?? GlucoseUnit.Mgdl;

            foreach (var watch in watches)
            {
                await JudgeAsync(person, watch, [.. states[watch.Id]], facts, unit, ct);
            }
        }
        finally
        {
            turn.Release();
        }
    }

    // Her own readings only: an imported one is history brought in, and can be minutes old when Home
    // Assistant's history reaches now.
    private async Task<GlucoseTriggerFacts> FactsAsync(
        SugaryDbContext db, Account person, GlucosePoll? poll, DateTimeOffset now, CancellationToken ct)
    {
        var hers = db.GlucoseReadings.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.UserId == person.UserId && r.TenantId == person.TenantId
                                                  && (r.Source == GlucoseSource.Current || r.Source == GlucoseSource.Graph));
        var since = now - GlucoseTriggers.Lookback;
        var recent = (await hers.Where(r => r.At >= since).OrderBy(r => r.At).ToListAsync(ct))
            .Select(r => new SensorReading(r.At, r.MgPerDl, r.Trend))
            .ToList();
        var stored = await hers.OrderByDescending(r => r.At).FirstOrDefaultAsync(ct);
        var newest = new[] { poll?.Newest, stored is null ? null : new SensorReading(stored.At, stored.MgPerDl, stored.Trend) }
            .OfType<SensorReading>()
            .MaxBy(r => r.At);

        var link = poll is not null
            ? new GlucoseLinkHealth(poll.Link.LinkedAt, poll.Link.LastSuccessAt, poll.Link.FailingSince)
            : await db.GlucoseLinks.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.UserId == person.UserId && l.TenantId == person.TenantId)
                .Select(l => new GlucoseLinkHealth(l.LinkedAt, l.LastSuccessAt, l.FailingSince))
                .FirstOrDefaultAsync(ct);

        // LibreLinkUp may list several sensors, all stamped by the poll that saw them: the one on her is
        // the latest started among those last seen.
        var sensors = await db.GlucoseSensors.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.UserId == person.UserId && s.TenantId == person.TenantId)
            .ToListAsync(ct);
        var lastSeen = sensors.Count == 0 ? (DateTimeOffset?)null : sensors.Max(s => s.LastSeenAt);
        var worn = sensors.Where(s => s.LastSeenAt == lastSeen).MaxBy(s => s.ActivatedAt);

        var zones = await db.PushSubscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.UserId == person.UserId && p.TenantId == person.TenantId)
            .Select(p => p.TimeZone)
            .ToListAsync(ct);

        return new GlucoseTriggerFacts
        {
            Now = now,
            Zone = calendar.OnSaved(zones).Zone,
            Occasion = poll is null ? GlucoseOccasion.Clock : GlucoseOccasion.Poll,
            Recent = recent,
            Newest = newest,
            Link = link,
            SensorActivatedAt = worn?.ActivatedAt
        };
    }

    private async Task JudgeAsync(
        Account person, GlucoseWatch watch, IReadOnlyList<GlucoseWatchState> rows, GlucoseTriggerFacts facts,
        GlucoseUnit unit, CancellationToken ct)
    {
        GlucoseWatchSpec spec;
        try
        {
            spec = GlucoseWatchSpec.Parse(watch.Spec);
        }
        catch (GlucoseWatchSpecException ex)
        {
            logger.LogWarning("Glucose watch {WatchId} of household {HouseholdId} no longer reads ({Reason}); not judged",
                watch.Id, watch.TenantId, ex.Message);
            return;
        }

        var prior = rows
            .Select(s => new GlucoseTriggerCondition(s.TriggerIndex, s.Holding, s.HoldingSince, s.FiredThisEpisode))
            .OrderBy(s => s.TriggerIndex)
            .ToList();
        var judgement = GlucoseTriggers.Judge(spec, prior, facts);
        if (judgement.Fired.Count == 0 && judgement.States.SequenceEqual(prior))
        {
            return;
        }

        var fired = new FiredGlucoseWatch(person, SqliteGlucoseWatches.ToStored(watch), spec)
        {
            Zone = facts.Zone,
            Unit = unit
        };
        var spent = false;
        foreach (var fire in judgement.Fired)
        {
            // A one-shot does what it does once, however many of its triggers fired together.
            if (spent)
            {
                break;
            }

            var outcome = await EffectsOfAsync(fired, fire, ct);
            spent = watch.Once && outcome.AnyDelivered;
        }

        await SaveAsync(watch, rows.Count, judgement, spent, facts.Now, ct);
    }

    // The episode counts as fired whatever its effects did, or a failing effect would be retried every
    // minute; only a `once` watch waits for one that reached somebody.
    private async Task<GlucoseEffectsOutcome> EffectsOfAsync(FiredGlucoseWatch watch, GlucoseTriggerFire fire, CancellationToken ct)
    {
        try
        {
            return await effects.FireAsync(watch, fire, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The effects of glucose watch {WatchId} of household {HouseholdId} failed on trigger {TriggerIndex}",
                watch.Watch.Id, watch.Subject.TenantId, fire.TriggerIndex);
            return GlucoseEffectsOutcome.None;
        }
    }

    private async Task SaveAsync(
        GlucoseWatch watch, int priorRows, GlucoseJudgement judgement, bool spent, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var fired = judgement.Fired.Count > 0;
        var still = await db.GlucoseWatches.IgnoreQueryFilters()
            .Where(w => w.TenantId == watch.TenantId && w.Id == watch.Id && w.Spec == watch.Spec && w.Enabled && !w.Spent)
            .ExecuteUpdateAsync(set =>
            {
                set.SetProperty(w => w.Spent, spent).SetProperty(w => w.Enabled, !spent);
                if (fired)
                {
                    set.SetProperty(w => w.LastFiredAt, now);
                }
            }, ct);
        // A rewrite clears the states it found, so rows gone (or come) since they were read are a rewrite
        // even where the spec came back word for word.
        var rows = db.GlucoseWatchStates.IgnoreQueryFilters().Where(s => s.TenantId == watch.TenantId && s.WatchId == watch.Id);
        if (still == 0 || await rows.CountAsync(ct) != priorRows)
        {
            logger.LogDebug("Glucose watch {WatchId} of household {HouseholdId} was rewritten or removed while judged; what was judged is dropped",
                watch.Id, watch.TenantId);
            return;
        }

        await rows.ExecuteDeleteAsync(ct);
        db.GlucoseWatchStates.AddRange(judgement.States.Select(s => new GlucoseWatchState
        {
            TenantId = watch.TenantId,
            WatchId = watch.Id,
            TriggerIndex = s.TriggerIndex,
            Holding = s.Holding,
            HoldingSince = s.HoldingSince,
            FiredThisEpisode = s.FiredThisEpisode
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}