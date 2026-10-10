use crate::led::LedState;
use tokio::sync::watch;
use tracing::warn;

/// Bound on how long music may stay ducked in the LISTENING state with no LED-state change. A mic
/// window the hub never closes would otherwise hold the music down until the connection drops; on
/// expiry the loop force-restores to full and waits for the next real state change.
const MAX_DUCK_SECS: u64 = 30;

/// The same bound for THINKING, which nothing on the satellite ends — only the hub's transcript
/// does — so it needs one, but a far longer one: a real agent turn (round trips, tool calls) can
/// run for minutes, and capping it short would flap the music up under a user who is waiting for
/// the answer. 120 s is the hub's own `FollowUp.ReplyTimeoutMs`, past which the hub gives up on the
/// turn and ends it anyway; led.rs blanks the ring on the same deadline, so a wedged turn recovers
/// its light and its music together.
const MAX_THINKING_DUCK_SECS: u64 = 120;

/// How long a ducked state may go unchanged before the loop force-restores to full, or None for
/// the states whose end is guaranteed by something other than a clock.
fn max_duck(state: LedState) -> Option<std::time::Duration> {
    match state {
        LedState::Listening => Some(std::time::Duration::from_secs(MAX_DUCK_SECS)),
        LedState::Thinking => Some(std::time::Duration::from_secs(MAX_THINKING_DUCK_SECS)),
        // Active playback, bounded by drain-completion — and a stuck one by connection teardown.
        LedState::Speaking => None,
        LedState::Idle => None, // never ducked
    }
}

enum DuckerBackend {
    Real { control: String, card: Option<String> },
    #[cfg(test)]
    Probe(std::sync::Arc<std::sync::Mutex<Vec<u8>>>),
}

impl DuckerBackend {
    async fn set(&mut self, pct: u8) -> anyhow::Result<()> {
        match self {
            DuckerBackend::Real { control, card } => {
                let cmd = match card {
                    Some(c) => format!("amixer -c {c} sset {control} {pct}%"),
                    None => format!("amixer sset {control} {pct}%"),
                };
                let status = crate::audio::build_command(&cmd)
                    .stdout(std::process::Stdio::null())
                    .stderr(std::process::Stdio::null())
                    .status()
                    .await?;
                anyhow::ensure!(status.success(), "amixer exited with {status}");
                Ok(())
            }
            #[cfg(test)]
            DuckerBackend::Probe(log) => {
                log.lock().unwrap().push(pct);
                Ok(())
            }
        }
    }
}

// Duck music for the WHOLE turn — Listening, Thinking and Speaking alike — and restore only on
// Idle. Listening MUST duck: with the speaker playing into the same room the mic would otherwise
// pick up the music and the hub's STT transcribes it, wrecking the command. Thinking ducks because
// the turn is not over: the user has asked something and is waiting for the answer, and the gap
// before it (an LLM round trip, often a tool call) regularly outlives the restore grace — restoring
// there brought the music up for a few seconds only to drop it again the moment the reply spoke.
// Idle is the one restoring state, and the satellite reaches it only when the hub ends the turn
// (transcript / arbitration loss): a stream draining mid-turn returns the LED to the turn's own
// phase, so the seams of a segmented answer never pass through Idle at all.
// (The max_duck timeouts in duck_loop still backstop a ducked state the hub never ends.)
fn target_percent(state: LedState, duck_percent: u8) -> u8 {
    match state {
        LedState::Listening | LedState::Thinking | LedState::Speaking => duck_percent,
        LedState::Idle => 100,
    }
}

async fn duck_loop(
    mut rx: watch::Receiver<LedState>,
    mut backend: DuckerBackend,
    duck_percent: u8,
    restore_grace: std::time::Duration,
) {
    let mut applied: Option<u8> = None;
    loop {
        let state = *rx.borrow_and_update();
        let pct = target_percent(state, duck_percent);
        tracing::debug!(?state, pct, ?applied, "music duck");
        if pct < 100 {
            // In a turn (Listening/Thinking/Speaking): duck immediately. The softvol "Music" control
            // is created lazily when snapclient first opens the `music` PCM; a very-early call can
            // fail here and disable ducking for this connection — Restart=always self-heals the next.
            if applied != Some(pct) {
                if let Err(e) = backend.set(pct).await {
                    warn!("music duck failed, ducking disabled for this connection: {e:#}");
                    return;
                }
                applied = Some(pct);
            }
            match max_duck(state) {
                // Speaking is active playback (plus its drain tail), bounded by drain-completion — a
                // reply legitimately holds it for its whole duration, tens of seconds for a long
                // answer. It must NOT be force-restored: capping it flapped the music up ~0.5 s
                // before a ~30 s reply's drain finished, then re-ducked on the follow-up chime. A
                // genuinely-stuck Speaking is bounded by connection teardown (DuckGuard restores on
                // drop), so wait with no deadline.
                None => { if rx.changed().await.is_err() { break; } }
                // Listening and Thinking can both wedge with no natural end (a mic window the hub
                // never closes, a reply that never comes), so bound the ducked wait with a safety
                // that force-restores rather than holding music down until the connection drops.
                Some(cap) => match tokio::time::timeout(cap, rx.changed()).await {
                    Ok(Ok(())) => {}
                    Ok(Err(_)) => break,
                    Err(_) => {
                        if let Err(e) = backend.set(100).await {
                            warn!("music duck restore failed, ducking disabled for this connection: {e:#}");
                            return;
                        }
                        applied = Some(100);
                        if rx.changed().await.is_err() { break; }
                    }
                },
            }
        } else if applied == Some(100) {
            // Already restored: wait for the next change, no deadline.
            if rx.changed().await.is_err() { break; }
        } else if applied.is_none() {
            // First evaluation and inactive: establish the un-ducked baseline immediately.
            if let Err(e) = backend.set(100).await {
                warn!("music duck failed, ducking disabled for this connection: {e:#}");
                return;
            }
            applied = Some(100);
            if rx.changed().await.is_err() { break; }
        } else {
            // Idle while ducked: DEBOUNCE the restore. Idle means the hub ended the turn, but a turn
            // that ends and immediately restarts (a follow-up the user answers at once, a queued
            // announcement) would otherwise flap the music up for the gap between them. Hold the duck
            // for restore_grace; if an in-turn state resumes within it, stay ducked. Only a genuinely
            // finished turn (grace elapses with nothing new) restores.
            match tokio::time::timeout(restore_grace, rx.changed()).await {
                Ok(Ok(())) => {}     // state changed => re-evaluate (may re-duck or keep waiting)
                Ok(Err(_)) => break, // sender dropped => connection ending
                Err(_) => {
                    if let Err(e) = backend.set(100).await {
                        warn!("music duck restore failed, ducking disabled for this connection: {e:#}");
                        return;
                    }
                    applied = Some(100);
                    if rx.changed().await.is_err() { break; }
                }
            }
        }
    }
}

pub struct DuckGuard {
    handle: tokio::task::JoinHandle<()>,
    control: String,
    card: Option<String>,
}

impl Drop for DuckGuard {
    fn drop(&mut self) {
        self.handle.abort();
        // Fail-safe restore to full volume. abort() drops the task future at its await point and
        // skips async cleanup, so this MUST be synchronous: fire a detached std amixer (never awaited).
        let mut cmd = std::process::Command::new("amixer");
        if let Some(c) = &self.card {
            cmd.arg("-c").arg(c);
        }
        cmd.args(["sset", &self.control, "100%"]);
        crate::audio::run_detached(cmd);
    }
}

pub fn spawn_duck(
    rx: watch::Receiver<LedState>,
    music_mixer: Option<String>,
    music_card: Option<String>,
    duck_percent: u8,
    restore_grace: std::time::Duration,
) -> Option<DuckGuard> {
    let control = music_mixer?; // None => feature off (mirrors led::spawn_led returning None)
    let backend = DuckerBackend::Real { control: control.clone(), card: music_card.clone() };
    let handle = tokio::spawn(duck_loop(rx, backend, duck_percent, restore_grace));
    Some(DuckGuard { handle, control, card: music_card })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::led::LedState;
    use std::sync::{Arc, Mutex};
    use tokio::sync::watch;

    fn probe() -> (Arc<Mutex<Vec<u8>>>, DuckerBackend) {
        let log = Arc::new(Mutex::new(Vec::new()));
        (log.clone(), DuckerBackend::Probe(log))
    }

    #[test]
    fn ducks_through_the_whole_turn_and_restores_only_on_idle() {
        // Every in-turn phase ducks — Listening (the mic must not hear the music), Thinking (the
        // user is waiting on an answer, not on the music) and Speaking (the reply must be audible
        // over it). Only Idle, which the satellite reaches at turn end, restores.
        assert_eq!(target_percent(LedState::Idle, 20), 100);
        assert_eq!(target_percent(LedState::Listening, 20), 20);
        assert_eq!(target_percent(LedState::Thinking, 20), 20);
        assert_eq!(target_percent(LedState::Speaking, 20), 20);
        assert_eq!(target_percent(LedState::Listening, 0), 0); // honors duck_percent (0 = mute)
    }

    async fn wait_for(log: &Arc<Mutex<Vec<u8>>>, len: usize) {
        for _ in 0..1000 {
            if log.lock().unwrap().len() >= len { return; }
            tokio::task::yield_now().await;
        }
        panic!("timed out waiting for duck call #{len}; got {:?}", log.lock().unwrap());
    }

    // Long enough that non-paused tests never hit it; paused tests advance past it.
    const TEST_GRACE: std::time::Duration = std::time::Duration::from_secs(2);

    // Let the spawned duck_loop run until it parks (on a watch change or a timeout), so a following
    // assert / time-advance observes a settled state. (Single-threaded test runtime.)
    async fn settle() {
        for _ in 0..50 { tokio::task::yield_now().await; }
    }

    #[tokio::test]
    async fn ducks_immediately_on_active_and_baselines_on_idle() {
        // Duck is immediate on an active state; the first Idle establishes the un-ducked baseline;
        // an active->active change keeps the same duck level (no redundant set).
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, std::time::Duration::from_secs(600)));
        wait_for(&log, 1).await;
        assert_eq!(log.lock().unwrap()[0], 100); // Idle baseline, immediate
        tx.send(LedState::Speaking).unwrap();
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20); // ducked immediately
        tx.send(LedState::Listening).unwrap(); // still active -> stays 20 (no new set)
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2);
        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn restore_is_debounced_after_a_reply_ends() {
        // A finished reply (Speaking -> Idle) does NOT restore immediately; it holds the duck for
        // restore_grace, then restores. Same debounce bounds a no-reply turn's strand.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));
        wait_for(&log, 1).await;
        assert_eq!(log.lock().unwrap()[0], 100);
        tx.send(LedState::Speaking).unwrap();
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);
        tx.send(LedState::Idle).unwrap(); // reply ends -> debounced, NOT immediate
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "un-duck is debounced");
        tokio::time::advance(TEST_GRACE / 2).await;
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "still ducked mid-grace");
        tokio::time::advance(TEST_GRACE).await; // past the grace
        wait_for(&log, 3).await;
        assert_eq!(log.lock().unwrap()[2], 100);
        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn stays_ducked_across_a_gap_between_playbacks() {
        // The debounce itself: playback stops, drops to Idle, and something starts speaking again
        // within the grace (a turn that ends into a queued announcement, a follow-up the user
        // answers at once). That gap must NOT flap the music back up.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));
        wait_for(&log, 1).await;
        assert_eq!(log.lock().unwrap()[0], 100);
        tx.send(LedState::Speaking).unwrap(); // segment 1
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);
        tx.send(LedState::Idle).unwrap(); // inter-segment gap
        settle().await;
        tokio::time::advance(TEST_GRACE / 2).await; // partway through the grace
        settle().await;
        tx.send(LedState::Speaking).unwrap(); // segment 2 resumes within the grace
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "no restore across the gap — stayed ducked");
        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn stays_ducked_while_the_agent_thinks() {
        // THE requirement: the gap between the user finishing their sentence and the reply starting
        // is the agent working, and the music must stay down across it however long it takes. An
        // agent turn routinely outlasts both the restore grace (a round trip, let alone a tool call)
        // and the Listening safety cap, and restoring in that window flapped the music up under a
        // user who is waiting for an answer, then straight back down when the reply spoke.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));
        wait_for(&log, 1).await;
        assert_eq!(log.lock().unwrap()[0], 100);

        tx.send(LedState::Listening).unwrap(); // user speaks
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);

        tx.send(LedState::Thinking).unwrap(); // hub endpointed the utterance; the agent is working
        settle().await;
        tokio::time::advance(TEST_GRACE * 2).await; // well past the restore debounce
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "the grace must not un-duck a thinking agent");
        tokio::time::advance(std::time::Duration::from_secs(MAX_DUCK_SECS + 5)).await; // slow turn
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "a long think must not hit the Listening cap");

        tx.send(LedState::Speaking).unwrap(); // the answer finally starts
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "still ducked — no flap between think and speak");

        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn turn_end_restores_after_grace() {
        // Idle is the one restoring state, and the satellite only reaches it at turn end (the hub's
        // transcript) — including a turn the agent never answered by voice. The restore is still
        // debounced, since that delay is what bridges the gaps a turn is made of.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));
        wait_for(&log, 1).await;
        assert_eq!(log.lock().unwrap()[0], 100);
        tx.send(LedState::Listening).unwrap();
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);
        tx.send(LedState::Thinking).unwrap(); // command captured, agent working
        settle().await;
        tx.send(LedState::Idle).unwrap(); // transcript: turn over, nothing was spoken
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "debounced, not instant");
        tokio::time::advance(TEST_GRACE + std::time::Duration::from_millis(1)).await;
        wait_for(&log, 3).await;
        assert_eq!(log.lock().unwrap()[2], 100);
        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn wedged_thinking_force_restores_after_the_hub_reply_timeout() {
        // Safety backstop for the new ducked Thinking: nothing on the satellite ends that phase —
        // only the hub's transcript does — so a hub that goes quiet mid-turn would hold the music
        // down until the connection drops. Cap it at the hub's own 120 s reply timeout, past which
        // the hub has given up on the turn too (EndConversation) and the ring has gone dark.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));
        wait_for(&log, 1).await;
        assert_eq!(log.lock().unwrap()[0], 100);

        tx.send(LedState::Thinking).unwrap();
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);

        tokio::time::advance(std::time::Duration::from_secs(MAX_THINKING_DUCK_SECS - 1)).await;
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "must outlast any real agent turn");
        const { assert!(MAX_THINKING_DUCK_SECS >= 120, "the cap must not undercut the hub's reply timeout") };

        tokio::time::advance(std::time::Duration::from_secs(2)).await; // past the cap
        wait_for(&log, 3).await;
        assert_eq!(log.lock().unwrap()[2], 100);

        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn long_speaking_reply_is_not_force_restored_by_max_duck() {
        // A single long reply holds Speaking continuously for longer than MAX_DUCK (a ~30 s spoken
        // answer streams as one uninterrupted playback job). The safety cap must NOT fire on active
        // playback — it is bounded by drain-completion — because force-restoring mid-reply flaps the
        // music up under the agent's voice, then re-ducks on the follow-up chime. Only a wedged
        // Listening window (a mic the hub never closes) is capped; a stuck Speaking is bounded by
        // connection teardown (DuckGuard).
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));

        wait_for(&log, 1).await; // initial Idle -> 100
        assert_eq!(log.lock().unwrap()[0], 100);

        tx.send(LedState::Speaking).unwrap(); // reply audio starts
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);

        tokio::time::advance(std::time::Duration::from_secs(MAX_DUCK_SECS + 5)).await; // long reply
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "Speaking must not be force-restored mid-reply");

        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn ducked_state_force_restores_after_max_duck() {
        // Safety backstop: a wedged ACTIVE state must not hold music ducked forever — after MAX_DUCK
        // with no state change it force-restores to full.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));

        wait_for(&log, 1).await; // initial Idle -> 100
        assert_eq!(log.lock().unwrap()[0], 100);

        tx.send(LedState::Listening).unwrap(); // command -> duck
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);

        tokio::time::advance(std::time::Duration::from_secs(MAX_DUCK_SECS + 1)).await;
        wait_for(&log, 3).await; // no change for MAX_DUCK -> safety restore
        assert_eq!(log.lock().unwrap()[2], 100);

        drop(tx);
        let _ = h.await;
    }
}