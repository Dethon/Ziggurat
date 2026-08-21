using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// A satellite's seat at the arbitration table, carrying exactly what arbitration reads and nothing
// else — so someone changing the satellite session can tell from this type whether arbitration is
// affected. Capture activity and pause support are read through delegates because both change
// after the handle is registered: the mic opens and closes per turn, and a satellite only proves it
// understands pause-satellite when it first reports wake_rms.
public sealed record WakeArbiterHandle(
    SatelliteIdentity Identity,
    double RmsOffsetDb,
    Func<bool> SupportsPause,
    Func<CaptureActivity?> CaptureActivity,
    Func<bool> TryAbortCapture,
    Func<CancellationToken, Task> PauseAsync,
    Func<CancellationToken, Task> EndLegacyAsync);

// Cross-satellite wake arbitration seat. Claims arrive synchronously on each connection's
// Wyoming read loop; the decision runs later on its own task, so the read loops never wait.
// Every claimant has already opened its capture — losing costs a discarded capture, never audio.
public sealed class WakeArbiter(
    ArbitrationSettings settings,
    VoiceConversationManager conversations,
    IMetricsPublisher metrics,
    TimeProvider time,
    ILogger<WakeArbiter> logger)
{
    private readonly Dictionary<string, WakeArbiterHandle> _handles = new();
    private readonly Lock _gate = new();
    private List<WakeClaim>? _window;

    // Deadline after which the arbiter stops waiting on a re-arm write and moves on (see
    // SendReArmAsync for how it is enforced — the write itself is abandoned, not cancelled).
    // WyomingWriter.WriteAsync on a half-open TCP socket BLOCKS rather than throwing, so an
    // unbounded await would stall the decision task with the remaining losers still live and
    // answering — the exact failure this feature exists to prevent, reached without any exception
    // for the catch to see. A re-arm is a few bytes to a LAN satellite, so anything slower is a dead
    // peer. Deliberately a constant, not a config knob — a liveness backstop is not a tuning
    // parameter.
    private const int ReArmWriteTimeoutMs = 2000;

    public void Register(string satelliteId, WakeArbiterHandle handle)
    {
        lock (_gate)
        {
            _handles[satelliteId] = handle;
        }
    }

    public void Unregister(string satelliteId)
    {
        lock (_gate)
        {
            _handles.Remove(satelliteId);
        }
    }

    // Is this satellite still a candidate to win a wake? Asked by the connection's unwind test,
    // which has no other way to see that a dropped link stopped competing before its playback loop
    // finished draining.
    internal bool IsRegistered(string satelliteId)
    {
        lock (_gate)
        {
            return _handles.ContainsKey(satelliteId);
        }
    }

    // Is a claim still waiting to be decided? Asked by the multi-satellite host test, which has to
    // let one satellite's solo window close before the next claims — otherwise the second joins the
    // first's window and the outcome is a loudness comparison rather than the steal under test.
    // Nothing on the wire marks a solo window closing, and the alternative was to sleep a multiple
    // of the window and hope, which is the shape that made that test intermittent.
    internal bool IsDeciding
    {
        get
        {
            lock (_gate)
            {
                return _window is not null;
            }
        }
    }

    public void Claim(string satelliteId, double? wakeRms, double? wakeScore, string source)
    {
        if (!settings.Enabled)
        {
            return;
        }
        lock (_gate)
        {
            if (_handles.Count < 2)
            {
                return;
            }
            var claim = new WakeClaim(satelliteId, wakeRms, wakeScore, source, time.GetTimestamp());
            if (_window is not null)
            {
                if (_window.All(c => c.SatelliteId != satelliteId))
                {
                    _window.Add(claim);
                }
                return;
            }
            _window = [claim];
        }
        _ = DecideAfterWindowAsync();
    }

    private async Task DecideAfterWindowAsync()
    {
        List<WakeClaim>? claims = null;
        Dictionary<string, WakeArbiterHandle> handles;
        try
        {
            // Floored at 1ms deliberately. At 0 the delay completes synchronously and the whole
            // decision — Redis publishes and satellite writes — runs inline on the Wyoming read
            // loop that called Claim; below 0 Task.Delay throws and, caught below, silently
            // disables arbitration for good. Neither is a setting anyone means to express.
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, settings.WindowMs)), time);
            lock (_gate)
            {
                claims = _window;
                _window = null;
                handles = new Dictionary<string, WakeArbiterHandle>(_handles);
            }
            if (claims is not null)
            {
                await DecideAsync(claims, handles);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Wake arbitration decision failed for {Claims}",
                string.Join(", ", (claims ?? []).Select(c => c.SatelliteId)));
            // Only clear a window we never took. Once claims is non-null the slot is already ours-
            // cleared, so any window there now belongs to a claim that arrived during this decision
            // and has its own decision task pending — nulling it would silently drop that wake.
            if (claims is null)
            {
                lock (_gate)
                {
                    _window = null;
                }
            }
        }
    }

    private async Task DecideAsync(List<WakeClaim> claims, Dictionary<string, WakeArbiterHandle> handles)
    {
        // Snapshot every non-claimant's capture history BEFORE the first await. ChunkHistory evicts
        // relative to its newest sample and Rule B reaches back past the wake-word span — over a
        // second of history — while the loser loop below awaits a Redis publish and a bounded wire
        // write per loser. Read after those, one slow peer is enough to age the holder's window out:
        // HasAlignedOnset then finds no onset, and the winner and the holder both dispatch the same
        // utterance as two conversations, with no exception and no log line to show for it.
        var openCaptures = handles
            .Where(kv => claims.All(c => c.SatelliteId != kv.Key))
            .Select(kv => (kv.Key, Handle: kv.Value, Activity: kv.Value.CaptureActivity()))
            .Where(h => h.Activity is not null)
            .ToList();

        var candidates = claims
            .Where(c => handles.ContainsKey(c.SatelliteId))
            .Select(c => new ArbitrationCandidate(c, c.WakeRms is { } rms
                ? WakeArbitrationRules.Calibrate(rms, handles[c.SatelliteId].RmsOffsetDb)
                : null))
            .ToList();
        // Every claimant may have disconnected inside the window, and PickWinner ends in First().
        if (candidates.Count == 0)
        {
            return;
        }

        var winner = WakeArbitrationRules.PickWinner(candidates);
        foreach (var loser in candidates.Where(c => !ReferenceEquals(c, winner)))
        {
            // Isolate each loser: one satellite failing to be suppressed must never cost the
            // others theirs, because every un-suppressed loser is a satellite that answers.
            // Rule B still has to run afterwards, so nothing here may escape this loop.
            try
            {
                await SuppressAsync(handles[loser.Claim.SatelliteId], loser.Claim, "lost_loudness");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to suppress arbitration loser {Id}",
                    loser.Claim.SatelliteId);
            }
        }

        if (winner.Claim.Source == WakeArbitrationRules.ButtonSource)
        {
            return; // deliberate physical intent: never suppressed, never a leak
        }

        var frequency = time.TimestampFrequency;
        var (spanStart, spanEnd) = WakeArbitrationRules.WakeWordSpan(
            winner.Claim.ReceivedAt, frequency, settings);
        var slack = WakeArbitrationRules.MsToTicks(settings.AlignSlackMs, frequency);
        var holder = openCaptures
            .Where(h => WakeArbitrationRules.HasAlignedOnset(
                h.Activity!, spanStart, spanEnd, frequency, settings))
            .Select(h => (h.Key, h.Handle, Peak: WakeArbitrationRules.Calibrate(
                WakeArbitrationRules.SpanPeakRms(h.Activity!, spanStart - slack, spanEnd + slack),
                h.Handle.RmsOffsetDb)))
            .OrderByDescending(h => h.Peak)
            .Select(h => ((string, WakeArbiterHandle, double)?)h)
            .FirstOrDefault();
        if (holder is not { } aligned)
        {
            return; // no other mic heard this utterance: the winner just proceeds
        }

        var (holderId, holderHandle, holderPeak) = aligned;
        if (winner.CalibratedRms is { } challenger
            && WakeArbitrationRules.CanSteal(challenger, holderPeak, settings.StealMarginDb))
        {
            // Only a capture we actually aborted may be stolen from: if it already ended
            // naturally, its dispatch is in flight and these were independent turns.
            if (!holderHandle.TryAbortCapture())
            {
                return;
            }
            // Commit the recoverable half BEFORE the wire write. The abort above is irreversible and
            // TransferBinding is a lock-guarded dictionary swap with no I/O, so pairing them keeps
            // the handoff atomic: a re-arm that fails or times out then costs the holder only a
            // silent re-arm, not the user's conversation continuity. Written the other way round, a
            // dead holder socket left the capture abandoned, the conversation stranded on the
            // holder until idle expiry, and no WakeHandoff recorded at all.
            // TransferBinding declines a stale handoff (the winner bound its own conversation
            // after the claim — its turn already ran while this decision was delayed): the
            // holder still lost its leaked capture and gets re-armed, but nothing moved, so the
            // record is a holder suppression, not a handoff. NothingToMove is the opposite fact and
            // stays a handoff: the holder was on its first utterance, so it had no conversation to
            // carry over — the ordinary field steal, and reporting it as a staleness would blame a
            // late decision that never happened.
            var transfer = conversations.TransferBinding(
                holderId, winner.Claim.SatelliteId, winner.Claim.ReceivedAt);
            metrics.Publish(transfer is BindingTransfer.DeclinedStale
                ? new VoiceEvent
                {
                    Metric = VoiceMetric.WakeSuppressed,
                    Outcome = "stale_steal"
                }.About(holderHandle.Identity)
                : new VoiceEvent
                {
                    Metric = VoiceMetric.WakeHandoff,
                    Outcome = holderId,
                    WakeRms = winner.Claim.WakeRms,
                    WakeScore = winner.Claim.WakeScore
                }.About(handles[winner.Claim.SatelliteId].Identity));
            await SendReArmAsync(holderHandle);
            return;
        }

        // The wake word leaked into the holder's already-open mic and the holder heard it
        // louder (or the challenger can't prove otherwise): the holder keeps the turn.
        await SuppressAsync(handles[winner.Claim.SatelliteId], winner.Claim, "leak");
    }

    private async Task SuppressAsync(WakeArbiterHandle handle, WakeClaim claim, string outcome)
    {
        if (!handle.TryAbortCapture())
        {
            logger.LogWarning(
                "Arbitration loser {Id} had no abortable capture (ended early); letting it proceed",
                claim.SatelliteId);
            return;
        }
        // Metric before the wire write, for the same reason the steal transfers first: the abort is
        // already irreversible, so the record of what happened must not hinge on reaching the peer.
        metrics.Publish(new VoiceEvent
        {
            Metric = VoiceMetric.WakeSuppressed,
            Outcome = outcome,
            WakeRms = claim.WakeRms,
            WakeScore = claim.WakeScore
        }.About(handle.Identity));
        await SendReArmAsync(handle, claim);
    }

    // Best-effort towards the peer, but never at the cost of the decision: whatever happens here,
    // the loser's capture is already settled locally and the remaining work must still run.
    private async Task SendReArmAsync(WakeArbiterHandle handle, WakeClaim? claim = null)
    {
        using var cts = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(ReArmWriteTimeoutMs), time);
        // Declared out here only so the timeout branch can adopt the write it abandons. Assigned
        // inside the try because the callbacks can throw synchronously (WyomingClient.WriteAsync
        // throws outright when the connection is gone) and that must stay caught here.
        Task? write = null;
        try
        {
            // WaitAsync, not merely the token: WyomingWriter honors cancellation at its send lock
            // and then writes the frame with CancellationToken.None by design (a mid-frame cancel
            // would desync the stream), so handing it the token bounds the wait for the lock and
            // nothing else. Abandoning the task is what actually bounds this call; the write's own
            // finally still releases the lock whenever it eventually completes or fails.
            write = handle.SupportsPause()
                ? handle.PauseAsync(cts.Token)
                : handle.EndLegacyAsync(cts.Token);
            await write.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Nothing awaits the abandoned write any more, so adopt whatever it eventually does:
            // an unclaimed fault resurfaces much later as a process-global UnobservedTaskException
            // with no connection left to the satellite that caused it.
            _ = write?.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            // A re-arm that THROWS is survivable: the socket is broken, so the read loop faults, the
            // connection drops, and the satellite resets itself to Idle when hub_rx closes. A re-arm
            // that TIMES OUT is not: the connection stays up, the hub's capture is gone, and the
            // satellite sits in Mode::Streaming, where it does not feed its wake detector at all
            // (satellite/src/satellite/state_machine.rs) and has no streaming timeout of its own.
            // That satellite is deaf until something else drops the connection, so this is an Error,
            // and it gets its own WakeSuppressed row — a different fact from why a loser lost, hence
            // a distinct Outcome, and emitted only on the timeout so a suppressed loser whose socket
            // simply threw is still counted exactly once.
            logger.LogError(
                "Re-arm write to satellite {Id} timed out after {TimeoutMs}ms — its connection is "
                + "still up but it stays in streaming mode and will not wake again until it reconnects",
                handle.Identity.SatelliteId, ReArmWriteTimeoutMs);
            metrics.Publish(new VoiceEvent
            {
                Metric = VoiceMetric.WakeSuppressed,
                Outcome = "rearm_failed",
                WakeRms = claim?.WakeRms,
                WakeScore = claim?.WakeScore
            }.About(handle.Identity));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Re-arm write to satellite {Id} failed",
                handle.Identity.SatelliteId);
        }
    }
}