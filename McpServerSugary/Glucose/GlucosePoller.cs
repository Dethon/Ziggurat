using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Glucose;

// Reads every live link — someone chosen to follow, and a password LibreLinkUp has not refused — once a
// period, and keeps what it answered as her readings. Each link has its own moment of the period (from
// its owner's id), so links are polled spread across it rather than all at once, and at most a few at
// a time. What LibreLinkUp answered is the link's state; how long to wait after a failure is this
// process's, kept per link: a refused password stops the link until she links again (asking every
// minute is what gets an account locked), a rate limit waits what the service said, and anything else
// is asked about less and less often while it lasts.
//
// Every owner's links and readings are read here, so the factory's context is used and every query
// says IgnoreQueryFilters with the owner spelled out.
public sealed class GlucosePoller(
    IDbContextFactory<SugaryDbContext> contexts,
    LibreLinkUpClient libreLinkUp,
    GlucoseCredentials credentials,
    IGlucosePollObserver observer,
    GlucoseSettings settings,
    TimeProvider clock,
    ILogger<GlucosePoller> logger)
{
    // How many links are asked at once: a household's handful never waits, and a deployment's many
    // never become a burst at the service.
    private const int MaxConcurrentPolls = 4;

    // The longest a link that keeps failing waits between asks, so an outage's end is seen within it.
    private static readonly TimeSpan _longestBackoff = TimeSpan.FromMinutes(15);

    // A rate limit that did not say how long to wait.
    private static readonly TimeSpan _rateLimitedWait = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<LinkKey, Pace> _paces = new();
    private readonly SemaphoreSlim _asking = new(MaxConcurrentPolls, MaxConcurrentPolls);

    public TimeSpan Period => TimeSpan.FromSeconds(Math.Max(1, settings.PollSeconds));

    // Polls every live link whose moment has come and is not being polled already, and returns when
    // those polls are done. A link seen for the first time is polled at its next moment.
    public async Task PollDueAsync(CancellationToken ct)
    {
        var links = await LiveLinksAsync(ct);
        var now = clock.GetUtcNow();
        var live = links.Select(link => link.Key).ToHashSet();
        foreach (var gone in _paces.Keys.Where(key => !live.Contains(key)))
        {
            _paces.TryRemove(gone, out _);
        }

        var due = links
            .Select(link => (Link: link, Pace: _paces.GetOrAdd(link.Key, key => new Pace(NextMoment(key.UserId, now)))))
            .Where(polled => polled.Pace.TryClaim(now, NextMoment(polled.Link.Key.UserId, now)))
            .ToList();
        await Task.WhenAll(due.Select(polled => PollAsync(polled.Link, polled.Pace, ct)));
    }

    private async Task<IReadOnlyList<LiveLink>> LiveLinksAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.GlucoseLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.PatientId != null && (l.LastFailure == null || l.LastFailure != GlucoseLinkFailure.CredentialsRefused))
            .Select(l => new LiveLink(
                new LinkKey(l.UserId, l.TenantId, l.LinkedAt, l.PatientId!), l.Email, l.SealedPassword, l.Region))
            .ToListAsync(ct);
    }

    private async Task PollAsync(LiveLink link, Pace pace, CancellationToken ct)
    {
        try
        {
            await _asking.WaitAsync(ct);
            try
            {
                if (await AskAsync(link, pace, ct) is { } poll)
                {
                    await observer.PolledAsync(poll, ct);
                }
            }
            finally
            {
                _asking.Release();
            }
        }
        // Hers alone: whatever failed — the store, or her watches after the poll — touches nobody else's.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Polling the LibreLinkUp link of {User}, or what follows it, failed; it is asked again at its next moment",
                link.Key.UserId);
        }
        finally
        {
            pace.Release();
        }
    }

    private async Task<GlucosePoll?> AskAsync(LiveLink link, Pace pace, CancellationToken ct)
    {
        var person = link.Key.Person;
        if (credentials.Open(person, link.SealedPassword) is not { } password)
        {
            if (pace.WarnUnopenedOnce())
            {
                logger.LogWarning(
                    "The LibreLinkUp password of {User}'s link does not open with this server's sealing key; it is not polled until she links again",
                    link.Key.UserId);
            }

            return null;
        }

        // Never logged: the account is a record, and would print the password.
        var account = new LibreLinkUpAccount(link.Email, password, link.Region);
        var answer = await libreLinkUp.GraphAsync(account, link.Key.PatientId, ct);
        var now = clock.GetUtcNow();
        return answer switch
        {
            LibreLinkUpAnswer<LibreLinkUpGraph>.Ok ok => await KeepAsync(link, pace, ok.Value, now, ct),
            LibreLinkUpAnswer<LibreLinkUpGraph>.Failed failed => await RecordAsync(link, pace, failed.Failure, now, ct),
            _ => null
        };
    }

    // Her readings and sensors kept and the link marked answered, together, and only while the link is
    // still the one the poll read: a poll out while she linked again or chose another person would keep
    // somebody else's readings as hers.
    private async Task<GlucosePoll?> KeepAsync(
        LiveLink link, Pace pace, LibreLinkUpGraph graph, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var answered = await Still(db, link.Key).ExecuteUpdateAsync(set => set
            .SetProperty(l => l.LastSuccessAt, now)
            .SetProperty(l => l.LastFailure, (GlucoseLinkFailure?)null)
            .SetProperty(l => l.FailureDetail, (string?)null)
            .SetProperty(l => l.FailingSince, (DateTimeOffset?)null), ct);
        if (answered == 0)
        {
            logger.LogDebug("The link of {User} changed while it was polled; the answer is dropped", link.Key.UserId);
            return null;
        }

        var stored = await KeepReadingsAsync(db, link.Key, graph, ct);
        await KeepSensorsAsync(db, link.Key, graph, now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (pace.Answered())
        {
            logger.LogInformation("LibreLinkUp answers the link of {User} again", link.Key.UserId);
        }

        return new GlucosePoll(
            link.Key.Person,
            stored,
            new GlucoseLinkState(link.Key.LinkedAt, now, null, null, null),
            graph.Connection.Current ?? graph.Points.LastOrDefault());
    }

    // The current reading is kept whenever the sensor has measured since: each is its own minute, and
    // at a poll a minute keeping only one every few minutes would thin the log the trend and the
    // statistics read. The curve is those same minutes thinned to five, so a curve point within
    // GlucoseReadingRules.Spacing of a reading she has — the current one of this poll included — is
    // the same moment seen twice and is not kept; one filling a gap (before the link, while her phone
    // was out of reach) is. Curve points carry no arrow: only the current reading says where she was
    // heading.
    private static async Task<IReadOnlyList<SensorReading>> KeepReadingsAsync(
        SugaryDbContext db, LinkKey key, LibreLinkUpGraph graph, CancellationToken ct)
    {
        var current = graph.Connection.Current;
        var points = graph.Points.Select(point => point with { Trend = GlucoseTrend.Unknown }).OrderBy(p => p.At).ToList();
        var shown = points.Select(p => p.At).Concat(current is null ? [] : [current.At]).ToList();
        if (shown.Count == 0)
        {
            return [];
        }

        var spacing = GlucoseReadingRules.Spacing;
        var from = shown.Min() - spacing;
        var to = shown.Max() + spacing;
        var instants = new SortedSet<DateTimeOffset>(await db.GlucoseReadings.IgnoreQueryFilters()
            .Where(r => r.UserId == key.UserId && r.TenantId == key.TenantId && r.At >= from && r.At <= to)
            .Select(r => r.At)
            .ToListAsync(ct));

        IReadOnlyList<(SensorReading Reading, GlucoseSource Source)> currentKept =
            current is not null && instants.Add(current.At) ? [(current, GlucoseSource.Current)] : [];
        var kept = currentKept
            .Concat(points
                .Where(point => instants.GetViewBetween(point.At - spacing + TimeSpan.FromTicks(1), point.At + spacing - TimeSpan.FromTicks(1)).Count == 0
                    && instants.Add(point.At))
                .Select(point => (Reading: point, Source: GlucoseSource.Graph)))
            .ToList();

        db.GlucoseReadings.AddRange(kept.Select(k => new GlucoseReading
        {
            UserId = key.UserId,
            TenantId = key.TenantId,
            At = k.Reading.At,
            MgPerDl = k.Reading.MgPerDl,
            Trend = k.Reading.Trend,
            Source = k.Source
        }));
        return [.. kept.Select(k => k.Reading).OrderBy(r => r.At)];
    }

    private static async Task KeepSensorsAsync(
        SugaryDbContext db, LinkKey key, LibreLinkUpGraph graph, DateTimeOffset now, CancellationToken ct)
    {
        var seen = graph.Sensors.Append(graph.Connection.Sensor).OfType<LibreLinkUpSensor>()
            .DistinctBy(s => s.Serial).ToList();
        var serials = seen.Select(s => s.Serial).ToList();
        var known = await db.GlucoseSensors.IgnoreQueryFilters()
            .Where(s => s.UserId == key.UserId && s.TenantId == key.TenantId && serials.Contains(s.Serial))
            .ToDictionaryAsync(s => s.Serial, ct);
        foreach (var sensor in seen)
        {
            if (!known.TryGetValue(sensor.Serial, out var row))
            {
                row = new GlucoseSensor { UserId = key.UserId, TenantId = key.TenantId, Serial = sensor.Serial };
                db.GlucoseSensors.Add(row);
            }

            row.ActivatedAt = sensor.ActivatedAt;
            row.ProductType = sensor.ProductType ?? row.ProductType;
            row.LastSeenAt = now;
        }
    }

    // The failure written onto the link, its start kept from the first failure of the run, and how long
    // this link now waits.
    private async Task<GlucosePoll?> RecordAsync(
        LiveLink link, Pace pace, LibreLinkUpFailure failure, DateTimeOffset now, CancellationToken ct)
    {
        var (kind, detail) = failure switch
        {
            LibreLinkUpFailure.CredentialsRefused => (GlucoseLinkFailure.CredentialsRefused, null),
            LibreLinkUpFailure.StepRequired step => (GlucoseLinkFailure.StepRequired, step.Component),
            LibreLinkUpFailure.VersionTooOld old => (GlucoseLinkFailure.VersionTooOld, old.MinimumVersion),
            LibreLinkUpFailure.RateLimited => (GlucoseLinkFailure.RateLimited, (string?)null),
            _ => (GlucoseLinkFailure.Unreachable, (string?)null)
        };

        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var written = await Still(db, link.Key).ExecuteUpdateAsync(set => set
            .SetProperty(l => l.LastFailure, kind)
            .SetProperty(l => l.FailureDetail, detail)
            .SetProperty(l => l.FailingSince, l => l.FailingSince ?? now), ct);
        if (written == 0)
        {
            logger.LogDebug("The link of {User} changed while it was polled; the failure is dropped", link.Key.UserId);
            return null;
        }

        var state = await Still(db, link.Key)
            .Select(l => new GlucoseLinkState(l.LinkedAt, l.LastSuccessAt, l.LastFailure, l.FailureDetail, l.FailingSince))
            .SingleAsync(ct);
        await transaction.CommitAsync(ct);

        var wait = pace.Failed(failure, Period, _longestBackoff, _rateLimitedWait, now);
        if (state.FailingSince == now)
        {
            logger.LogWarning(
                "LibreLinkUp gave the link of {User} no answer ({Failure}: {Reason}); asked again in {Wait}",
                link.Key.UserId, kind, failure is LibreLinkUpFailure.Unreachable unreachable ? unreachable.Reason : detail, wait);
        }

        return new GlucosePoll(link.Key.Person, [], state, null);
    }

    private static IQueryable<GlucoseLink> Still(SugaryDbContext db, LinkKey key) =>
        db.GlucoseLinks.IgnoreQueryFilters().Where(l =>
            l.UserId == key.UserId && l.TenantId == key.TenantId && l.LinkedAt == key.LinkedAt && l.PatientId == key.PatientId);

    // The link's own moment of the period strictly after now: an offset from the period's start fixed by
    // its owner's id, so links spread across the period and keep their place in it across restarts.
    private DateTimeOffset NextMoment(string userId, DateTimeOffset now)
    {
        var period = Period.Ticks;
        var offset = (long)(BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(userId))) % (ulong)period);
        var sinceMoment = now.UtcTicks - offset;
        return new DateTimeOffset(sinceMoment - sinceMoment % period + period + offset, TimeSpan.Zero);
    }

    // A link as the poller knows it: a link linked again, or pointed at another person, is another key,
    // so it starts afresh and an answer for the old one is never written onto it.
    private sealed record LinkKey(string UserId, string TenantId, DateTimeOffset LinkedAt, string PatientId)
    {
        public Account Person => new(TenantId, UserId);
    }

    private sealed record LiveLink(LinkKey Key, string Email, byte[] SealedPassword, string? Region);

    // When a link is next asked, whether it is being asked now, and how many asks in a row failed.
    private sealed class Pace(DateTimeOffset nextAt)
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _nextAt = nextAt;
        private bool _asking;
        private int _failures;
        private bool _warnedUnopened;

        public bool TryClaim(DateTimeOffset now, DateTimeOffset nextMoment)
        {
            lock (_gate)
            {
                if (_asking || now < _nextAt)
                {
                    return false;
                }

                _asking = true;
                _nextAt = nextMoment;
                return true;
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _asking = false;
            }
        }

        // True when it had been failing: an outage's end is worth a line in the log.
        public bool Answered()
        {
            lock (_gate)
            {
                var wasFailing = _failures > 0;
                _failures = 0;
                return wasFailing;
            }
        }

        public TimeSpan Failed(
            LibreLinkUpFailure failure, TimeSpan period, TimeSpan longest, TimeSpan rateLimited, DateTimeOffset now)
        {
            lock (_gate)
            {
                _failures++;
                var wait = failure is LibreLinkUpFailure.RateLimited limited
                    ? limited.RetryAfter ?? rateLimited
                    : TimeSpan.FromTicks(Math.Min(longest.Ticks, period.Ticks << Math.Min(_failures - 1, 20)));
                _nextAt = _nextAt > now + wait ? _nextAt : now + wait;
                return wait;
            }
        }

        public bool WarnUnopenedOnce()
        {
            lock (_gate)
            {
                var first = !_warnedUnopened;
                _warnedUnopened = true;
                return first;
            }
        }
    }
}