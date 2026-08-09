---
paths:
  - "satellite/src/music.rs"
  - "satellite/src/volume.rs"
  - "scripts/provision-satellite-rs.sh"
---

# Satellite Music Ducking & Local Speaker Volume

Moved from `satellite/CLAUDE.md`; the crate-wide invariants and the wire protocol stay there.

## Music ducking

`music.rs` rides the same `LedState` watch channel as the ring (`--music-mixer`, an ALSA softvol
set with `amixer`; absent = feature off). **Every in-turn phase ducks — Listening, Thinking and
Speaking — and only Idle restores**, because a turn is one continuous thing from the user's side:
the wait between their sentence and the answer is an LLM round trip plus any tool calls, routinely
far longer than the restore grace, and restoring across it brought the music up for a few seconds
under a user waiting for a reply, then dropped it again the instant the reply spoke. Ducking
through Thinking is therefore deliberate — don't "fix" it back to restore-on-Thinking.

The satellite reaches Idle only when the hub ends the turn (`transcript`, or `pause-satellite` on
arbitration loss); a stream draining mid-turn returns to the turn's `phase`, so the seams of a
segmented answer never touch Idle. Two guards survive on top of that:

- **Restore grace** (`--music-restore-grace-ms`, default 3000) debounces the un-duck on Idle, so a
  turn that ends and immediately restarts doesn't flap.
- **Per-state duck caps** (`max_duck`) force-restore a ducked state the hub never ends: 30 s for
  Listening (a mic window left open), 120 s for Thinking — the hub's own `FollowUp.ReplyTimeoutMs`,
  the same deadline `led.rs` blanks the ring on, so a wedged turn gets its light and its music back
  together. Speaking is **uncapped**: it is bounded by drain-completion (and by connection teardown
  via `DuckGuard`), and a cap there flapped the music up ~0.5 s before a ~30 s reply finished.

## Local speaker volume

`volume.rs` drives the satellite's MASTER output level on the hub's `speaker-volume` event
(protocol 1.8, actions `up`/`down`/`mute`/`unmute`/`alert-hold`/`alert-release`), stepping on an
equal-dB grid sized by `--volume-step` (default 10 → ten 5.1 dB steps across −51..0 dB). Master is deliberately not one of the `Music`/`TTS`/`Alert`
softvols, which carry calibration and, in `Music`'s case, are rewritten by the ducker on every
turn. Master and softvol multiply, so ducking is untouched by this feature.

**Two backends, one master.** They are the same knob — the last thing every source passes
through — reached with whatever tool the unit has:

- **Music units**: `--volume-sink` names the PipeWire sink, driven with `wpctl`. Wireplumber
  persists its level and mute, so nothing is written to disk here and a level survives a restart.
- **Voice-only units**: `--volume-mixer` (plus `--volume-card`) names an ALSA softvol, driven with
  `amixer` — the same tool and shape as the music ducker. PipeWire is installed only on music
  units, so provisioning writes a software master into `/etc/asound.conf` instead and points
  `--snd-command` at it. Software is not a shortcut: an amp HAT like the MiniAmp has no hardware
  volume control at all, so on this hardware every level is software anyway.

**`--volume-step` is the same size on both: an equal-dB step.** A step moves 51·step/100 dB
(5.1 dB at the default 10) across a −51..0 dB range, so ten default steps span the whole range on
either unit type and the same spoken command sounds like the same change everywhere. The ALSA
softvol gets this for free — its taper is linear in dB from −51.0 to 0.0, so the relative
`amixer … 10%±` of the raw 0-255 range IS that step. The PipeWire scale is cubic
(`dB = 60·log10(display)`), so a blind relative `wpctl` bump would be ~2.7 dB near the top and
~18 dB near the bottom; instead a step there is read → snap → write: `wpctl get-volume`, snap the
level to the dB grid anchored at 0 dB, move one step, write it back absolutely
(`volume.rs::db_linear_step`). Snapping keeps repeated commands drift-free and heals a sink
someone moved by hand to the grid on the first command; a failed or unparsable read errors out
(warn, no cue) rather than guessing a level. Stepping down bottoms out at −51 dB rather than at
silence on both backends; only `mute` truly silences a unit.

A softvol provides one element, so the voice-only master is a pair: `Nabu Volume` (level) and
`Nabu Switch` (a resolution-2 softvol, which IS a mute switch). ALSA's simple mixer merges a
`<base> Volume` element with a `<base> Switch` one, so both are the single control `Nabu` and
`amixer sset Nabu 10%+` / `sset Nabu mute` drive them. `amixer` clamps a relative step to the
control's own range, which is what the dB math's 0 dB ceiling does for the PipeWire write (the
`-l 1.0` flag went with the relative command). Unlike wireplumber, nothing persists
this across a reboot: the control does not exist when `alsa-restore` runs, so softvol recreates it
at maximum on the satellite's first playback.

With neither flag the whole thing is a no-op with a warning, mirroring `music_mixer: None`.
Passing BOTH is rejected by `Config::parse`: they name two tools for the same master, so whichever
one lost would leave the satellite beeping its confirmation at a level nobody hears — exactly the
failure a voice-only unit had before it had a master at all.

Everything below is backend-independent — the gate, the tracked mute and the alert hold sit above
`Backend` and behave identically on both.

An internal `tokio::sync::Mutex` gate serializes `step`, `alert_hold`, `alert_release` and
`set_user_mute` end-to-end, across their own awaited mixer call. It was added because an alert
hold and a queued mute confirmation each read the tracked state, change it, and await a mixer
call — two separate windows a concurrent call could otherwise land inside, leaving `user_muted()`
disagreeing with whichever call's process actually finished last. The gate makes such calls run
one fully after the other, so the last one to finish decides both consistently.

**`user_muted` and `alert_held` both live on `VolumeControl`, which is process-scoped.** A hub
reconnect cannot forget the user's mute, and every decision reads the two together — that is the
whole point of keeping them in one place. `user_muted` is seeded once at boot by reading the
master (`wpctl get-volume`, which prints `[MUTED]`; `amixer sget`, which prints `[on]`/`[off]`),
so the mixer's restored state and ours agree. A failed or unparsable read leaves it unmuted, the
safe direction: the speaker is audible and one spoken command fixes it.

**An alarm must ring on a muted speaker.** `alert-hold` marks the hold and unmutes the sink
WITHOUT clearing `user_muted`, so the release has something to put back. It is idempotent: the hub
re-sends it at the top of every ring round, and a repeat while the hold already stands writes
nothing. A hold whose unmute call fails un-marks itself, so the next round's re-assert retries it.
There is no counter on this side — the hub counts overlapping alerts and sends `alert-release`
only when the last one covering this satellite ends.

**A mute that arrives during a ring is deferred, not applied.** `set_user_mute(true)` under an
outstanding hold records the intent and skips the sink write; `alert-release` writes it. Without
that, "silencia el altavoz" said a second before a timer fired silenced the timer: the mute
confirmation is queued behind its cue, so it landed ~300 ms after the hold had already unmuted.
An unmute always lands at once — it cannot silence anything, and it is what the user just asked
for.

`HoldGuard` covers a hub that dies mid-alarm. On connection teardown it ends the hold and fires a
detached mixer call putting the master back to `user_muted` (Drop cannot await, same shape as
`music.rs`'s `DuckGuard`). It runs both ways: the speaker is left audible if the user never muted,
and muted if their mute was still deferred by the hold.

**The mute cue must not be silenced by its own mute**: `mute` plays the cue via
`PlaybackHandle::cue_then` and applies the mute on the acknowledgement, which the pump sends after
`play_cue` drains — and also when it drops the cue for an active stream, so the mute still lands.
The wait is a detached task, never the `select!` loop.
