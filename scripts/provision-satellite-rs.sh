#!/usr/bin/env bash
set -euo pipefail

# Usage: scripts/provision-satellite-rs.sh <user@host> [mic-device]
#   Music satellite: MUSIC_HUB=<snapserver-host> MUSIC_ROOM=<player-name> [TTS_VOLUME=<pct>] \
#                    [ALERT_VOLUME=<pct>] scripts/provision-satellite-rs.sh <user@host>
#   Master level:    [MASTER_VOLUME=<pct>] (default 50) — the startup level of the master the
#                    spoken volume commands drive, on BOTH unit paths (the PipeWire sink on a
#                    music unit, the "Nabu" softvol on a voice-only one).
#   Wake tuning:     THRESHOLD=<0..1> WAKE_WINDOW=<n> scripts/provision-satellite-rs.sh <user@host>
#                    Defaults 0.7 / 2 — applied to BOTH unit paths (the voice-only ExecStart and
#                    the music drop-in that overrides it), so a music unit is tuned by the same
#                    two vars. WAKE_WINDOW is how many 80 ms classifier scores are AVERAGED before
#                    the mean is compared against THRESHOLD (it replaced TRIGGER_LEVEL, which
#                    counted consecutive frames and reset on any single dip — a jittery score
#                    trace under background TV or music never fired). TRIGGER_LEVEL is still read
#                    as an alias. LOWER threshold or window = easier wake but more false positives;
#                    window 1 with a low threshold is what made music itself trigger the wake word
#                    — retune one knob at a time. Above 3 the added wake latency exceeds the
#                    240 ms --wake-preroll-ms gap flush, so raise that too.
#                    WAKE_MIN_RMS=<i16 rms> (default 0 = off) drops a wake whose wake-word audio
#                    is quieter than that — the fix for wakes fired out of a near-silent room,
#                    which neither knob above reaches because those fires score as high as real
#                    ones. A per-microphone number: 350 is the measured figure for the XVF3800.
#                    WAKE_QUIET_RMS=<i16 rms> (default 0 = off) holds a QUIET room to a stricter
#                    rule: while the room's median level (last 30 s AND last 15 s) is under it,
#                    WAKE_QUIET_THRESHOLD (default 0.9) and WAKE_QUIET_WINDOW (default 2) replace
#                    THRESHOLD / WAKE_WINDOW. False fires are one-frame score blips that the
#                    lenient rule TV and music need lets through; nothing jitters a real wake in
#                    a quiet room, so the strict rule costs nothing there. Per-microphone too:
#                    200 is the measured figure for the XVF3800.
#   I2S DAC/amp HAT (e.g. MiniAmp): additionally DAC_OVERLAY=hifiberry-dac — I2S hardware is
#                    electrically undiscoverable, so the overlay must be declared; the script
#                    writes it to config.txt, reboots the Pi, waits, and continues (one-shot).
# Builds the static binary (via the fp16-shim-aware build script), copies it + the systemd
# unit to the Pi, and enables the service. A voice-only Pi needs only alsa-utils; a music Pi also
# gets snapclient + PipeWire — music, TTS and cues share ONE output, mixed by PipeWire (neither
# the bcm2835 jack nor an I2S DAC can ALSA-dmix). The output is auto-detected: an I2S DAC/amp
# HAT (HiFiBerry-class `sndrpihifiberry*` card, e.g. the MiniAmp driving a wired speaker) when
# present, else the 3.5mm jack. The mic card stays OUT of PipeWire, owned raw by the satellite.
# On music units the satellite's own playback flows through per-source ALSA softvols on the speaker
# card, under a master (the PipeWire sink) that provisioning starts at MASTER_VOLUME% (default 50)
# — calibration lives in the source knobs, and the master is the knob the spoken volume commands
# drive, so it starts halfway to leave those commands headroom in both directions:
#   TTS   (TTS_VOLUME%, default 100) agent voice + cues; the volume knob for amp HATs that have none
#   Alert (ALERT_VOLUME%, default 100) timers/alarms, so an alert bypasses the conversational level
#   Music (ducked live by the satellite for the whole turn: listening, thinking AND speaking)
# Tune live: amixer -c <card> sset TTS <pct>% / sset Alert <pct>% ; persist: sudo alsactl store.
# Wireplumber persists later master changes, so MASTER_VOLUME is only where a (re)provision puts
# it. Note an alert rings through master × Alert, so a lowered master quiets alarms too.
# A VOICE-ONLY unit has no PipeWire, so it gets its master as a software ALSA softvol instead:
# `pcm.speaker` (control "Nabu") in front of the output device, which everything the satellite
# plays goes through. That is what the hub's spoken volume/mute commands drive there — without it
# they would beep a confirmation and move nothing, since an amp HAT has no hardware volume. It is
# asserted to MASTER_VOLUME% unmuted on every provision, and — because a reboot wipes a softvol
# control, whose first open would recreate it at MAX — a nabu-volume-init boot oneshot re-lands
# it on the same level before the satellite starts, so every boot starts there too.
# NOTE: a re-provision without explicit TTS_VOLUME/MASTER_VOLUME comes back at these factory
# levels — pass a unit's calibrated values to keep its levels.
#
# Audio addressing:
#   - No [mic-device] arg (the usual case): the USB audio card is auto-detected by NAME from
#     `arecord -l` and addressed as plughw:CARD=<name>,DEV=0. By-name addressing is immune to
#     ALSA slot/index churn — the old `options snd_usb_audio index=0` pinning collided with the
#     Pi 4/5 built-in vc4-hdmi + headphone cards (slots 0-2), failing the USB probe with -16 so
#     no capture card was created. The mic is 16 kHz mono native (what the satellite wants, no
#     resampling); `plughw` resamples only the 22050 Hz playback to a rate the device supports.
#   - With a [mic-device] arg: used verbatim on both commands. Use this for a device that is
#     already 16 kHz/22050-friendly, e.g. the reSpeaker 2-Mic HAT plughw:CARD=seeed2micvoicec,
#     DEV=0 (also add --button-gpio 17 to ExecStart).
#
# One mic quirk is handled automatically:
#   - Some USB mics only run capture while their OWN playback stream is active (both UAC
#     endpoints Synchronous off one internal clock) — capture-alone opens EIO and a live capture
#     dies the instant playback stops. A probe below detects this and installs
#     nabu-micclock.service, an endless zero-stream into the mic card's (unconnected) output
#     that keeps capture clocked 24/7.
#     This was the reSpeaker XVF3800 on firmware <=2.0.6; its 2.0.10 fixed it (see
#     satellite/CLAUDE.md for the version check + DFU flash), so a unit on current firmware
#     probes clean and gets no feeder. The probe stays for other UAC hardware.
#     The probe is only valid on a COLD engine (warm engines pass capture-alone for minutes), so
#     the verdict is sticky across re-provisions — and a FIRST provision should run against a
#     cold-booted Pi, not one whose mic was just in use.

host=${1:?need user@host}
mic=${2:-}     # empty => auto-detect the USB audio card, address it by name

# Wake tuning. Validated HERE, before the (slow) build and any remote change: these values are
# spliced into the unit's ExecStart, so a malformed one would install a satellite that fails to
# start — a bricked Pi is a much worse failure than a rejected argument.
threshold=${THRESHOLD:-0.7}
# TRIGGER_LEVEL is the pre-sliding-window name, still honoured so muscle memory (and any wrapper
# script) keeps working instead of being silently ignored; WAKE_WINDOW wins when both are set.
wake_window=${WAKE_WINDOW:-${TRIGGER_LEVEL:-2}}
[[ $threshold =~ ^(0(\.[0-9]+)?|1(\.0+)?)$ ]] || { echo "ERROR: THRESHOLD must be a number in 0..1 (got '$threshold')" >&2; exit 1; }
[[ $wake_window =~ ^[1-9][0-9]*$ ]] || { echo "ERROR: WAKE_WINDOW must be a positive integer (got '$wake_window')" >&2; exit 1; }
wake_min_rms=${WAKE_MIN_RMS:-0}
[[ $wake_min_rms =~ ^[0-9]+(\.[0-9]+)?$ ]] || { echo "ERROR: WAKE_MIN_RMS must be a number >= 0 (got '$wake_min_rms')" >&2; exit 1; }
wake_quiet_rms=${WAKE_QUIET_RMS:-0}
wake_quiet_threshold=${WAKE_QUIET_THRESHOLD:-0.9}
wake_quiet_window=${WAKE_QUIET_WINDOW:-2}
[[ $wake_quiet_rms =~ ^[0-9]+(\.[0-9]+)?$ ]] || { echo "ERROR: WAKE_QUIET_RMS must be a number >= 0 (got '$wake_quiet_rms')" >&2; exit 1; }
# Strictly above 0, unlike THRESHOLD: the binary refuses a quiet threshold of 0.
[[ $wake_quiet_threshold =~ ^(0\.[0-9]*[1-9][0-9]*|1(\.0+)?)$ ]] || { echo "ERROR: WAKE_QUIET_THRESHOLD must be a number in (0, 1] (got '$wake_quiet_threshold')" >&2; exit 1; }
[[ $wake_quiet_window =~ ^[1-9][0-9]*$ ]] || { echo "ERROR: WAKE_QUIET_WINDOW must be a positive integer (got '$wake_quiet_window')" >&2; exit 1; }

# Softvol levels, validated here for the same reason: a malformed one fails at `amixer` under
# `set -e` only AFTER asound.conf and the PipeWire master have already been rewritten, leaving a
# half-provisioned unit to repair over SSH.
tts_volume=${TTS_VOLUME:-100}
alert_volume=${ALERT_VOLUME:-100}
master_volume=${MASTER_VOLUME:-50}
[[ $tts_volume =~ ^([0-9]|[1-9][0-9]|100)$ ]] || { echo "ERROR: TTS_VOLUME must be an integer in 0..100 (got '$tts_volume')" >&2; exit 1; }
[[ $alert_volume =~ ^([0-9]|[1-9][0-9]|100)$ ]] || { echo "ERROR: ALERT_VOLUME must be an integer in 0..100 (got '$alert_volume')" >&2; exit 1; }
[[ $master_volume =~ ^([0-9]|[1-9][0-9]|100)$ ]] || { echo "ERROR: MASTER_VOLUME must be an integer in 0..100 (got '$master_volume')" >&2; exit 1; }

"$(dirname "$0")/../satellite/scripts/build-release.sh"
bin="$(dirname "$0")/../satellite/target/aarch64-unknown-linux-musl/release/nabu-satellite"

# Multiplex every ssh/scp below over ONE shared connection (ControlMaster), so password auth is
# entered exactly once instead of per-connection. The first command opens the master; the rest
# reuse the socket. The trap closes it (ControlPersist is just a safety net if the script dies).
ctl=$(mktemp -u "${TMPDIR:-/tmp}/nabu-ssh-XXXXXX")
SSHOPTS=(-o ControlMaster=auto -o ControlPath="$ctl" -o ControlPersist=120)
trap 'ssh -o ControlPath="$ctl" -O exit "$host" 2>/dev/null || true; rm -f "$ctl"' EXIT

# --- I2S DAC/amp HAT overlay (opt-in via DAC_OVERLAY, e.g. hifiberry-dac for the MiniAmp) ---
# I2S hardware is electrically undiscoverable: without its dtoverlay in config.txt the card
# never exists and provisioning would silently fall back to routing the speaker through the
# mic card / jack. Declaring the overlay keeps a fresh Pi one-shot: write it, reboot (scheduled
# via systemd-run so the remote script exits 9 cleanly instead of dying with the connection;
# the dropped ControlMaster re-opens on the next ssh), wait for the card, continue.
if [ -n "${DAC_OVERLAY:-}" ]; then
  rc=0
  ssh "${SSHOPTS[@]}" "$host" DAC_OVERLAY="${DAC_OVERLAY}" bash -se <<'OVEOF' || rc=$?
    set -euo pipefail
    cfg=/boot/firmware/config.txt
    [ -f "$cfg" ] || cfg=/boot/config.txt   # pre-bookworm layout
    if aplay -l 2>/dev/null | grep -q sndrpihifiberry; then
      echo "I2S DAC card already present; overlay OK"
    elif grep -q "^dtoverlay=${DAC_OVERLAY}\b" "$cfg"; then
      echo "ERROR: dtoverlay=${DAC_OVERLAY} already in $cfg but no sndrpihifiberry card after boot — check the HAT seating/wiring." >&2
      exit 1
    else
      echo "Enabling dtoverlay=${DAC_OVERLAY} in $cfg; rebooting the Pi..."
      printf '\n# nabu satellite speaker (added by provision-satellite-rs.sh)\ndtoverlay=%s\n' \
        "$DAC_OVERLAY" | sudo tee -a "$cfg" >/dev/null
      sudo systemd-run --on-active=2 systemctl reboot >/dev/null 2>&1
      exit 9
    fi
OVEOF
  if [ "$rc" = 9 ]; then
    echo "Waiting for the Pi to come back with the I2S DAC card..."
    sleep 10
    tries=0
    until ssh "${SSHOPTS[@]}" -o ConnectTimeout=3 "$host" 'aplay -l 2>/dev/null | grep -q sndrpihifiberry' 2>/dev/null; do
      tries=$((tries + 1))
      if [ "$tries" -gt 60 ]; then
        echo "ERROR: Pi did not come back with an sndrpihifiberry card within ~5 min of the overlay reboot." >&2
        exit 1
      fi
      sleep 5
    done
    echo "I2S DAC card is up."
  elif [ "$rc" != 0 ]; then
    exit "$rc"
  fi
fi

ssh "${SSHOPTS[@]}" "$host" "sudo apt-get install -y alsa-utils"   # arecord/aplay only; no Python
scp "${SSHOPTS[@]}" "$bin" "$host:/tmp/nabu-satellite"
scp "${SSHOPTS[@]}" "$(dirname "$0")/../satellite/deploy/nabu-satellite.service" "$host:/tmp/"
scp "${SSHOPTS[@]}" "$(dirname "$0")/../satellite/deploy/snapclient.service" "$host:/tmp/"
scp "${SSHOPTS[@]}" "$(dirname "$0")/../satellite/deploy/nabu-micclock.service" "$host:/tmp/"

# Quoted heredoc + MIC/MUSIC_HUB/MUSIC_ROOM/TTS_VOLUME/ALERT_VOLUME/MASTER_VOLUME/THRESHOLD/
# WAKE_WINDOW/WAKE_MIN_RMS/WAKE_QUIET_* env vars: nothing is expanded locally; the remote bash evaluates everything (and
# reads vars from the command-prefix assignments). The volume and tuning vars arrive already
# defaulted and validated above (WAKE_WINDOW having absorbed any TRIGGER_LEVEL alias).
ssh "${SSHOPTS[@]}" "$host" MIC="${mic}" MUSIC_HUB="${MUSIC_HUB:-}" MUSIC_ROOM="${MUSIC_ROOM:-}" TTS_VOLUME="${tts_volume}" ALERT_VOLUME="${alert_volume}" MASTER_VOLUME="${master_volume}" THRESHOLD="${threshold}" WAKE_WINDOW="${wake_window}" WAKE_MIN_RMS="${wake_min_rms}" WAKE_QUIET_RMS="${wake_quiet_rms}" WAKE_QUIET_THRESHOLD="${wake_quiet_threshold}" WAKE_QUIET_WINDOW="${wake_quiet_window}" bash -se <<'EOF'
  set -euo pipefail

  if [ -n "${MUSIC_HUB:-}" ] && [ -n "${MIC}" ]; then
    echo "ERROR: music provisioning (MUSIC_HUB) supports only the auto-detected USB card; do not also pass a mic-device." >&2
    exit 1
  fi
  sudo install -m755 /tmp/nabu-satellite /usr/local/bin/nabu-satellite
  user=$(whoami)
  sudo usermod -aG gpio,input "$user"     # GPIO button + evdev/USB button access

  # Drop artifacts from any prior provisioning run, plus any systemd drop-in (e.g. a temporary
  # mic-command override) — it would shadow the ExecStart we write here, so re-provisioning must
  # clear it for the unit below to be authoritative. nabu-alsa.conf is the old, broken
  # index-pinning; snd-usb-audio.conf is dead nrpacks=1 latency tuning (the parameter no longer
  # exists; the kernel logs "unknown parameter ignored"); 99-nabu-jabra.rules belongs to
  # retired hardware. All three are removed so a re-provisioned Pi carries no orphans.
  sudo rm -f /etc/modprobe.d/nabu-alsa.conf /etc/modprobe.d/snd-usb-audio.conf \
             /etc/udev/rules.d/99-nabu-jabra.rules
  sudo rm -rf /etc/systemd/system/nabu-satellite.service.d

  if [ -z "${MIC}" ]; then
    # arecord -l lists only capture-capable cards, so the Pi's output-only vc4-hdmi/headphone
    # devices never appear — the USB mic is the lone entry on a dedicated satellite.
    al=$(arecord -l)
    cardname=$(printf '%s\n' "$al" | sed -nE 's/^card [0-9]+: ([^ ]+) \[.*/\1/p' | sed -n '1p')
    cardidx=$(printf '%s\n' "$al" | sed -nE 's/^card ([0-9]+):.*/\1/p' | sed -n '1p')
    carddesc=$(printf '%s\n' "$al" | sed -nE 's/^card [0-9]+: [^ ]+ \[([^]]+)\].*/\1/p' | sed -n '1p')
    if [ -z "$cardname" ]; then
      echo "ERROR: no USB capture device in 'arecord -l' — is the mic plugged in?" >&2
      exit 1
    fi
    echo "Detected USB audio card: $cardname (ALSA index $cardidx)"

    # Address the mic by NAME (stable across reboots / ALSA index churn). The capture side is
    # 16 kHz mono native — exactly what the satellite wants, no resampling; `plughw` resamples
    # only the 22050 Hz playback to a rate the device supports. CAPTURE never goes through
    # /etc/asound.conf (an earlier 48 kHz plug device was wrong — this capture is 16 kHz-ONLY);
    # that file describes PLAYBACK only, and both branches below rewrite it in full.
    dev="plughw:CARD=${cardname},DEV=0"

    # Stop USB autosuspend during long idle, keyed on the detected device's id — good hygiene
    # for any USB-audio device.
    usbid=$(cat /proc/asound/card${cardidx}/usbid 2>/dev/null || true)
    if [ -n "$usbid" ]; then
      # A second, XVF3800-specific rule grants plugdev write access to drive the LED ring over
      # USB control transfers (without it nusb's open() fails and the ring stays dark). It is
      # keyed on the XVF3800's own vendor:product, NOT on $usbid — $usbid is whatever card
      # arecord -l detected, which could be a different mic entirely, and even on the XVF3800
      # itself MODE/GROUP would also widen access to its runtime DFU interface (firmware
      # reflashing) if applied generically.
      printf 'ACTION=="add", SUBSYSTEM=="usb", ATTR{idVendor}=="%s", ATTR{idProduct}=="%s", ATTR{power/control}="on"\n' \
        "${usbid%%:*}" "${usbid##*:}" | sudo tee /etc/udev/rules.d/99-nabu-usb-audio.rules >/dev/null
      printf 'ACTION=="add", SUBSYSTEM=="usb", ATTR{idVendor}=="2886", ATTR{idProduct}=="001a", MODE="0660", GROUP="plugdev"\n' \
        | sudo tee -a /etc/udev/rules.d/99-nabu-usb-audio.rules >/dev/null
      sudo udevadm control --reload
      sudo udevadm trigger --action=add
    fi
  else
    dev="${MIC}"   # explicit device, used verbatim (e.g. reSpeaker 2-Mic HAT)
  fi

  # --- implicit-clock mic probe (XVF3800-on-old-firmware class) ---
  # Some UAC mics only run capture while their OWN playback stream is active: both endpoints are
  # Synchronous off one internal clock and the firmware gates the capture engine on the output
  # stream — capture-alone opens EIO on read, and a live capture dies the instant playback stops
  # (verified on-device). Probe capture-alone; if it fails but works with a playback stream
  # running, install nabu-micclock.service: an endless zero-stream into the mic card's
  # (unconnected) speaker output that keeps capture clocked 24/7.
  # The reSpeaker XVF3800 behaved this way on firmware <=2.0.6 only; 2.0.10 fixed it (2026-07-27,
  # verified cold-boot + 17-min-idle + feeder-stopped-under-live-capture), so such a unit now
  # probes clean and the else-branch below removes any feeder a previous provision installed.
  # Stop the consumers first: a running satellite/feeder holds the device and would fake
  # the probe result.
  # The verdict is STICKY: the probe is only valid on a COLD engine. After the feeder has been
  # running, the engine stays clocked for a while, so capture-alone succeeds and a re-provision
  # would wrongly tear the feeder down (observed on-device 2026-07-16: capture streamed for
  # minutes after the feeder stopped). If a prior provision installed nabu-micclock, trust it;
  # a hardware swap needs `sudo rm /etc/systemd/system/nabu-micclock.service` before re-running.
  sudo systemctl stop nabu-satellite nabu-micclock 2>/dev/null || true
  micclock=0
  if [ -f /etc/systemd/system/nabu-micclock.service ]; then
    echo "nabu-micclock already provisioned: keeping it (probe is unreliable on a warm engine)"
    micclock=1
  elif ! timeout 5 arecord -D "${dev}" -r 16000 -c 1 -f S16_LE -t raw -d 1 /dev/null 2>/dev/null; then
    timeout 10 aplay -D "${dev}" -r 16000 -c 2 -f S16_LE -t raw /dev/zero 2>/dev/null &
    feeder=$!
    sleep 0.5
    if timeout 5 arecord -D "${dev}" -r 16000 -c 1 -f S16_LE -t raw -d 1 /dev/null 2>/dev/null; then
      micclock=1
    fi
    kill "$feeder" 2>/dev/null || true
    wait "$feeder" 2>/dev/null || true
  fi
  if [ "$micclock" = 1 ]; then
    echo "Mic needs its own playback stream running (XVF3800-class): installing nabu-micclock"
    sudo sed "s|MICDEV|${dev}|g; s|%i|$user|g" /tmp/nabu-micclock.service \
      | sudo tee /etc/systemd/system/nabu-micclock.service >/dev/null
    sudo systemctl daemon-reload
    sudo systemctl enable nabu-micclock.service
    sudo systemctl restart nabu-micclock.service
  else
    sudo systemctl disable --now nabu-micclock.service 2>/dev/null || true
    sudo rm -f /etc/systemd/system/nabu-micclock.service
  fi

  # --- output card: prefer an I2S DAC/amp HAT over the mic card / jack ---
  # HiFiBerry-class overlays (hifiberry-dac = MiniAmp, etc.) all register as sndrpihifiberry*.
  outcard=$(aplay -l | sed -nE 's/^card [0-9]+: (sndrpihifiberry[^ ]*) \[.*/\1/p' | sed -n '1p')
  if [ -n "$outcard" ]; then
    echo "Detected I2S DAC/amp HAT: $outcard (speaker output)"
    snddev="plughw:CARD=${outcard},DEV=0"
  else
    snddev="${dev}"   # no HAT: play through the mic card's own output
  fi
  # What the satellite's --snd-command actually opens. Same as the raw device unless the voice-only
  # branch below puts its softvol master in front of it.
  playdev="${snddev}"
  # The voice-only master's simple-control name, and the ONE place it is written: the asound.conf
  # elements, the amixer calls and the unit's --volume-mixer argument are all derived from it, so
  # renaming it cannot leave the satellite driving a control that no longer exists.
  volmixer="Nabu"
  # Set ONLY once this unit really has a working softvol master. Everything downstream (the unit's
  # volume flags) keys on this, so every way of not getting one — no card, or a chain that will not
  # open — lands on the identical "no local volume control, plain plughw playback" outcome.
  volume_ctl=""
  # A softvol CONTROL has to be stored on a real card, so pull the card name out of the device
  # string. Everything this script generates is `...CARD=<name>,DEV=0`; an explicit [mic-device]
  # in another form (hw:1,0) leaves nothing to hang a control on, and that unit then runs with no
  # local volume knob at all rather than with one that drives nothing.
  sndcard=$(printf '%s' "$snddev" | sed -nE 's/.*CARD=([^,]+).*/\1/p')

  # --- music coexistence (opt-in via MUSIC_HUB) ---
  # Music + TTS + cues all share the speaker output ($snddev), mixed by PipeWire in userspace
  # (neither the bcm2835 jack nor an I2S DAC can ALSA-dmix; 2nd client -> -22). The mic card is
  # kept OUT of PipeWire and owned by the satellite via raw ALSA (capture + the micclock feeder).
  # Clear any stale music drop-in first so a re-provision is deterministic (added back below if music).
  sudo rm -f /etc/systemd/system/nabu-satellite.service.d/pipewire.conf
  if [ -n "${MUSIC_HUB:-}" ]; then
    uid=$(id -u "$user")
    excltoken=$(printf '%s' "$carddesc" | awk '{print $1}')   # distinctive card word, e.g. "Array"
    # snapclient + PipeWire (the userspace mixer). snapclient + the satellite are SYSTEM services
    # with no login session, so enable lingering to run the user's PipeWire at boot and pass
    # XDG_RUNTIME_DIR into both units so their aplay/snapclient reach it.
    sudo apt-get install -y snapclient pipewire pipewire-alsa wireplumber
    sudo usermod -aG audio "$user"
    sudo loginctl enable-linger "$user"
    XDG_RUNTIME_DIR=/run/user/$uid systemctl --user enable --now pipewire pipewire-pulse wireplumber 2>/dev/null || true

    # Keep the mic card OUT of PipeWire so the satellite owns it raw (capture + the micclock
    # feeder). Without this PipeWire grabs the card — it even elects the
    # XVF3800's unconnected speaker jack as default sink — and raw ALSA opens fail EBUSY.
    # Match the detected card by its distinctive name word. When an I2S DAC HAT is the speaker,
    # also disable the unused HDMI + headphone-jack cards so the HAT is deterministically the
    # ONLY sink WirePlumber can elect as default.
    sudo mkdir -p /etc/wireplumber/wireplumber.conf.d
    hatrules=""
    if [ -n "$outcard" ]; then
      hatrules='
  {
    matches = [
      { device.name = "~alsa_card.platform-.*hdmi.*" },
      { device.name = "~alsa_card.platform-.*mailbox.*" }
    ]
    actions = { update-props = { device.disabled = true } }
  }'
    fi
    sudo tee /etc/wireplumber/wireplumber.conf.d/50-nabu-exclude-mic.conf >/dev/null <<WPEOF
monitor.alsa.rules = [
  {
    matches = [ { device.name = "~alsa_card.*${excltoken}.*" } ]
    actions = { update-props = { device.disabled = true } }
  }${hatrules}
]
WPEOF

    # `music` PCM: snapclient -> a softvol the satellite ducks (amixer -c <card> sset Music <pct>%)
    # -> PipeWire -> speaker. `tts` PCM: the satellite's own playback (replies + cues) -> a softvol
    # holding the calibrated agent-voice level (TTS_VOLUME) -> PipeWire -> speaker, independent
    # of music — the volume knob for amp HATs that have none (e.g. the MiniAmp). `alert` PCM: the
    # same shape at ALERT_VOLUME (default 100), used ONLY for hub-marked timer/alarm streams, so
    # an alert is not capped by the conversational voice level. All three CONTROLs are stored on
    # the speaker card (HAT when present, else the jack); the audio itself flows through PipeWire
    # either way. NO pcm.!default (PipeWire's own default stands); capture stays direct plughw.
    outctl="${outcard:-Headphones}"
    sudo tee /etc/asound.conf >/dev/null <<ASOUND
pcm.music {
    type softvol
    slave.pcm "pipewire"
    control { name "Music" card ${outctl} }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}

pcm.tts {
    type softvol
    slave.pcm "pipewire"
    control { name "TTS" card ${outctl} }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}

pcm.alert {
    type softvol
    slave.pcm "pipewire"
    control { name "Alert" card ${outctl} }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}
ASOUND

    # Upgrade hygiene: a prior voice-only install's boot oneshot targets pcm.speaker, which the
    # rewrite above just removed; PipeWire persistence takes over the master here.
    sudo systemctl disable --now nabu-volume-init.service 2>/dev/null || true
    sudo rm -f /etc/systemd/system/nabu-volume-init.service

    # Apply the rules (the speaker becomes the only/default sink) and set a sane output volume.
    XDG_RUNTIME_DIR=/run/user/$uid systemctl --user restart wireplumber 2>/dev/null || true
    sleep 3
    # Master at MASTER_VOLUME% (default 50): this is the knob the spoken volume commands drive, so
    # it starts halfway to leave them headroom in both directions; wireplumber persists the user's
    # later changes, so this is only where a (re)provision puts it. Deliberate trade-off: an alert
    # rings through master × Alert, so until the user raises the master an alarm rings at this
    # level too.
    XDG_RUNTIME_DIR=/run/user/$uid wpctl set-volume @DEFAULT_AUDIO_SINK@ "${MASTER_VOLUME}%" 2>/dev/null || true

    # Calibrate the source levels: a softvol CONTROL only materializes on first open of its PCM,
    # so play 1 s of silence through each, then set the level (re-asserted on every provision,
    # like the master above) and store ALSA state so it survives a power cut, not just a clean
    # shutdown. Runs as the login user: on Raspberry Pi OS the default user is in the `audio`
    # group at login (needed to create the control); a first-ever provision under a user only just
    # added to `audio` above would fail here — re-login (rerun the script) fixes it.
    XDG_RUNTIME_DIR=/run/user/$uid timeout 10 aplay -D tts -r 22050 -c 1 -f S16_LE -t raw -d 1 /dev/zero
    amixer -c "${outctl}" sset TTS "${TTS_VOLUME}%"
    XDG_RUNTIME_DIR=/run/user/$uid timeout 10 aplay -D alert -r 22050 -c 1 -f S16_LE -t raw -d 1 /dev/zero
    amixer -c "${outctl}" sset Alert "${ALERT_VOLUME}%"
    sudo alsactl store

    # snapclient -> the `music` softvol PCM (so the satellite can duck it); XDGDIR so its
    # ALSA->PipeWire bridge connects.
    sudo sed "s|%i|$user|g; s|HUBHOST|${MUSIC_HUB}|g; s|ROOM|${MUSIC_ROOM:-$cardname}|g; s|XDGDIR|/run/user/$uid|g" \
      /tmp/snapclient.service | sudo tee /etc/systemd/system/snapclient.service >/dev/null
    sudo systemctl daemon-reload
    sudo systemctl enable snapclient.service
    sudo systemctl restart snapclient.service

    # nabu-satellite drop-in: replies + cues -> the `tts` softvol (calibrated agent-voice level),
    # timer/alarm streams -> the `alert` softvol (ALERT_VOLUME, default 100) so they are not capped
    # by that level; both -> PipeWire (speaker). Ducks the `Music` softvol while active; XDG so
    # aplay reaches PipeWire. Overrides the base voice ExecStart.
    sudo mkdir -p /etc/systemd/system/nabu-satellite.service.d
    sudo tee /etc/systemd/system/nabu-satellite.service.d/pipewire.conf >/dev/null <<DROPEOF
[Service]
Environment=XDG_RUNTIME_DIR=/run/user/$uid
ExecStart=
ExecStart=/usr/local/bin/nabu-satellite \\
  --listen 0.0.0.0:10700 \\
  --mic-command "arecord -D ${dev} -r 16000 -c 1 -f S16_LE -t raw -F 20000" \\
  --snd-command "aplay -D tts -r 22050 -c 1 -f S16_LE -t raw --start-delay=100000 -F 50000" \\
  --alert-snd-command "aplay -D alert -r 22050 -c 1 -f S16_LE -t raw --start-delay=100000 -F 50000" \\
  --preroll-ms 1000 \\
  --music-mixer Music --music-card ${outctl} \\
  --volume-sink @DEFAULT_AUDIO_SINK@ \\
  --threshold ${THRESHOLD} \\
  --wake-window ${WAKE_WINDOW} \\
  --wake-min-rms ${WAKE_MIN_RMS} \\
  --wake-quiet-rms ${WAKE_QUIET_RMS} \\
  --wake-quiet-threshold ${WAKE_QUIET_THRESHOLD} \\
  --wake-quiet-window ${WAKE_QUIET_WINDOW}
DROPEOF
  else
    # downgrade / voice-only: tear down any prior music install so a re-provisioned Pi returns to
    # exactly the voice-only state (no orphaned snapclient / stale PipeWire routing config).
    sudo systemctl disable --now snapclient.service 2>/dev/null || true
    sudo rm -f /etc/systemd/system/snapclient.service
    sudo rm -f /etc/wireplumber/wireplumber.conf.d/50-nabu-exclude-mic.conf
    sudo systemctl daemon-reload

    # asound.conf used to be DELETED here: a voice-only unit played straight to plughw, so any file
    # present belonged to a previous music install. It now carries this unit's OWN master, so the
    # teardown became a full REWRITE — `tee` truncates, so a downgrade cannot leave pcm.music /
    # pcm.tts / pcm.alert behind either way. The one case with nothing to write still deletes.
    #
    # The master is a software softvol because there is nothing else to turn: the MiniAmp (and amp
    # HATs generally) have no hardware volume, and PipeWire — which is what gives a music unit its
    # master — is deliberately not installed here. `<name> Volume` and `<name> Switch` are one ALSA
    # simple control: the simple mixer merges a `<base> Volume` element with a `<base> Switch` one,
    # which is what gives the satellite both a level and a mute on a plugin that provides only a
    # single element each (a resolution-2 softvol IS the mute switch). Both sit in front of the
    # real device, so EVERYTHING the satellite plays — replies, cues and alerts alike
    # (--alert-snd-command defaults to --snd-command) — passes through them. A distinctive base
    # name keeps the pair from merging with a hardware `Master` on some other output card.
    if [ -n "$sndcard" ]; then
      sudo tee /etc/asound.conf >/dev/null <<ASOUND
pcm.speaker {
    type softvol
    slave.pcm "nabu_mute"
    control { name "${volmixer} Volume" card "${sndcard}" }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}

pcm.nabu_mute {
    type softvol
    slave.pcm "${snddev}"
    control { name "${volmixer} Switch" card "${sndcard}" }
    resolution 2
}
ASOUND

      # A softvol control only materializes on FIRST OPEN of its PCM, so open it once here (same
      # reason the music branch plays 1 s of silence through `tts`/`alert`) and assert the
      # master: MASTER_VOLUME% (default 50 — halfway, so the spoken volume commands have headroom
      # in both directions), unmuted because a re-provision must never hand back a silent speaker.
      # A reboot wipes the control regardless (it does not exist yet when alsa-restore runs, and
      # its first open would recreate it at MAX), so the nabu-volume-init oneshot installed below
      # re-lands it on the same level once per boot, BEFORE the satellite starts.
      #
      # THIS OPEN IS THE GATE, not a calibration nicety: it is the only chance to find out whether
      # the chain works on this hardware before the satellite depends on it. If it fails, revert to
      # raw plughw. A unit installed with --snd-command pointing at a PCM that will not open has NO
      # audio at all — playback errors are connection-fatal, so every cue and reply would drop the
      # hub connection and Restart=always would churn — which is strictly worse than the plain
      # working speaker this unit had before the feature existed. Losing the volume knob is not.
      # The aplay error text is echoed rather than swallowed, because it is what distinguishes a
      # busy device from a chain the card cannot negotiate. No retry: the realistic holder is the
      # micclock feeder on a unit whose output IS the mic card, which is permanent, and which also
      # blocks the satellite's own playback — a retry would only delay the same fallback.
      if probe_err=$(timeout 10 aplay -D speaker -r 22050 -c 1 -f S16_LE -t raw -d 1 /dev/zero 2>&1 >/dev/null); then
        playdev="speaker"
        volume_ctl="${volmixer}"
        amixer -c "${sndcard}" sset "${volmixer}" "${MASTER_VOLUME}%" unmute >/dev/null \
          || echo "WARNING: could not set the '${volmixer}' master on card ${sndcard}"
        # amixer APPLIES `unmute` only if the element actually has a switch and says nothing when
        # it does not, so the line above cannot report a Volume/Switch pair that failed to merge.
        # Read the state back instead: `[on]`/`[off]` in sget is the switch, and it is also exactly
        # what the satellite parses at startup to seed the user's mute.
        amixer -c "${sndcard}" sget "${volmixer}" 2>/dev/null | grep -qE '\[(on|off)\]' \
          || echo "WARNING: the '${volmixer}' master on card ${sndcard} has no mute switch — spoken volume will work, spoken mute will not"
        sudo alsactl store || true
        # Once per BOOT (not per service start): re-materialize the control and land the master on
        # its startup level before the satellite runs. RemainAfterExit keeps the unit active, so a
        # satellite crash-restart cannot re-trigger it and stomp a level the user set by voice; the
        # trailing `|| true` keeps a busy device from parking the unit in failed — the satellite's
        # own first playback then recreates the control at MAX, which is exactly today's fallback.
        sudo tee /etc/systemd/system/nabu-volume-init.service >/dev/null <<UNITEOF
[Unit]
Description=Land the nabu speaker master on its startup level
After=alsa-restore.service sound.target
Before=nabu-satellite.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/bin/sh -c 'timeout 10 aplay -D speaker -r 22050 -c 1 -f S16_LE -t raw -d 1 /dev/zero 2>/dev/null; amixer -c ${sndcard} sset ${volmixer} ${MASTER_VOLUME}% unmute || true'

[Install]
WantedBy=multi-user.target
UNITEOF
        sudo systemctl enable nabu-volume-init.service
      else
        echo "WARNING: the softvol master PCM would not open, so LOCAL VOLUME CONTROL IS DISABLED on this unit;"
        echo "         playback falls back to ${snddev} and the satellite keeps working. aplay said: ${probe_err}"
        sudo rm -f /etc/asound.conf
        sudo systemctl disable --now nabu-volume-init.service 2>/dev/null || true
        sudo rm -f /etc/systemd/system/nabu-volume-init.service
      fi
    else
      echo "WARNING: no ALSA card name in '${snddev}' — this unit gets no local speaker volume control"
      sudo rm -f /etc/asound.conf
      sudo systemctl disable --now nabu-volume-init.service 2>/dev/null || true
      sudo rm -f /etc/systemd/system/nabu-volume-init.service
    fi
  fi

  # Template the base (voice) unit: mic + snd devices -> the detected plughw (the snd side being
  # the softvol master PCM when this unit has one), wake tuning -> the THRESHOLD/WAKE_WINDOW vars,
  # %i -> user. Music units additionally carry the pipewire.conf drop-in written above, which
  # overrides this ExecStart to route TTS/cues to PipeWire — it carries the same two tuning values,
  # so tuning applies either way, plus its own --volume-sink.
  # Every substitution rewrites the ARGUMENT rather than matching a literal default value, so
  # changing the unit template's defaults can't silently break provisioning (the tuning seds used
  # to match `--threshold 0.7` / `--trigger-level 2` verbatim and would have gone quietly inert
  # the moment either default moved). `[^ ]*` stops at the value token, leaving the ExecStart's
  # trailing ` \` continuation intact. All are anchored to the continuation lines (leading
  # whitespace) so the header COMMENTS, which mention the same flags and carry their own `-D`
  # examples, are left alone.
  # DEFAULT is to drop both volume flags: a --volume-mixer naming a control that does not exist is
  # exactly the "beeps but moves nothing" failure this feature exists to remove, so a unit only
  # gets them once $volume_ctl says it really has that control. Music units land here too — their
  # drop-in supplies --volume-sink instead, and the two are mutually exclusive. The control NAME is
  # rewritten like every other argument, so the unit file cannot drift from the asound.conf.
  volsed=(-e "/^[[:space:]]*--volume-mixer/d" -e "/^[[:space:]]*--volume-card/d")
  [ -z "$volume_ctl" ] || volsed=(-e "/^[[:space:]]*--volume-mixer/ s#--volume-mixer [^ ]*#--volume-mixer ${volume_ctl}#" \
                                  -e "/^[[:space:]]*--volume-card/ s#--volume-card [^ ]*#--volume-card ${sndcard}#")
  sudo sed -e "/^[[:space:]]*--mic-command/ s#-D [^ ]*#-D ${dev}#" \
           -e "/^[[:space:]]*--snd-command/ s#-D [^ ]*#-D ${playdev}#" \
           -e "/^[[:space:]]*--threshold/ s#--threshold [^ ]*#--threshold ${THRESHOLD}#" \
           -e "/^[[:space:]]*--wake-window/ s#--wake-window [^ ]*#--wake-window ${WAKE_WINDOW}#" \
           -e "/^[[:space:]]*--wake-min-rms/ s#--wake-min-rms [^ ]*#--wake-min-rms ${WAKE_MIN_RMS}#" \
           -e "/^[[:space:]]*--wake-quiet-rms/ s#--wake-quiet-rms [^ ]*#--wake-quiet-rms ${WAKE_QUIET_RMS}#" \
           -e "/^[[:space:]]*--wake-quiet-threshold/ s#--wake-quiet-threshold [^ ]*#--wake-quiet-threshold ${WAKE_QUIET_THRESHOLD}#" \
           -e "/^[[:space:]]*--wake-quiet-window/ s#--wake-quiet-window [^ ]*#--wake-quiet-window ${WAKE_QUIET_WINDOW}#" \
           "${volsed[@]}" \
           -e "s/%i/$user/g" \
           /tmp/nabu-satellite.service | sudo tee /etc/systemd/system/nabu-satellite.service >/dev/null

  # restart (not just enable --now): re-provisioning must pick up the new unit even when the
  # service is already running.
  sudo systemctl daemon-reload
  sudo systemctl enable nabu-satellite.service
  sudo systemctl restart nabu-satellite.service
  echo "Provisioned. Logs: journalctl -u nabu-satellite -f"
EOF
