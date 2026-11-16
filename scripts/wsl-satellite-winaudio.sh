#!/usr/bin/env bash
set -euo pipefail

# Run the Rust nabu-satellite on this WSL host with NATIVE WINDOWS AUDIO.
#
# Why: WSLg's RDP audio bridge (RDPSink/RDPSource) audibly degrades playback — the
# Linux-side pipeline measures bit-clean at the Pulse monitor tap, yet the same PCM
# sounds crackly/harsh through the RDP leg while native Windows playback of the same
# bytes is perfect. So instead of paplay/parecord against WSLg Pulse, this variant
# spawns WINDOWS ffmpeg/ffplay through WSL interop: the satellite still runs in WSL
# (the dockerized hub still dials tcp://host.docker.internal:SAT_PORT, same as
# wsl-satellite.sh), but mic capture is dshow and playback renders via WASAPI —
# no RDP bridge anywhere. Zero satellite code changes: it just spawns commands.
#
# Needs a Windows ffmpeg with ffplay under %LOCALAPPDATA%\nabu-satellite (the
# gyan.dev release-essentials zip extracted there).
#
# Usage:
#   ./scripts/wsl-satellite-winaudio.sh
#   SAT_PORT=10600 THRESHOLD=0.6 MIC_DEVICE="Microphone (USB Audio Device)" \
#     ./scripts/wsl-satellite-winaudio.sh
#
# MIC_DEVICE is the dshow device name; leave unset to auto-pick the first capture
# device (the script prints the choice and all candidates).

SAT_PORT="${SAT_PORT:-10800}"
THRESHOLD="${THRESHOLD:-0.5}"
MIC_DEVICE="${MIC_DEVICE:-}"

repo="$(cd "$(dirname "$0")/.." && pwd)"
bin="$repo/satellite/target/release/nabu-satellite"

# Locate the Windows-side ffmpeg/ffplay (no PATH dependency).
localappdata="$(/mnt/c/Windows/System32/cmd.exe /c "echo %LOCALAPPDATA%" 2>/dev/null | tr -d '\r')"
tooldir="$(wslpath "$localappdata")/nabu-satellite"
ffplay="$(find "$tooldir" -name ffplay.exe 2>/dev/null | head -1)"
ffmpeg="$(find "$tooldir" -name ffmpeg.exe 2>/dev/null | head -1)"
if [[ -z "$ffplay" || -z "$ffmpeg" ]]; then
  echo "ffmpeg/ffplay not found under $tooldir" >&2
  echo "Download https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" >&2
  echo "and extract it to %LOCALAPPDATA%\\nabu-satellite\\" >&2
  exit 1
fi

# dshow device names live on stderr of -list_devices; lines look like:
#   [in#0 @ ...] "Microphone (USB Audio Device)" (audio)
# (the [...] log prefix varies across ffmpeg versions; CRs must go before the $ anchor)
list_mics() {
  "$ffmpeg" -hide_banner -list_devices true -f dshow -i dummy 2>&1 \
    | tr -d '\r' | sed -n 's/^\[[^]]*\] *"\(.*\)" (audio)$/\1/p'
}

if [[ -z "$MIC_DEVICE" ]]; then
  mapfile -t mics < <(list_mics)
  if [[ ${#mics[@]} -eq 0 ]]; then
    echo "no dshow audio capture devices found" >&2
    exit 1
  fi
  MIC_DEVICE="${mics[0]}"
  echo "auto-selected mic: $MIC_DEVICE"
  if [[ ${#mics[@]} -gt 1 ]]; then
    echo "other capture devices (override with MIC_DEVICE=...):"
    printf '  %s\n' "${mics[@]:1}"
  fi
fi

( cd "$repo/satellite" && cargo build --release )

# True when something is LISTENing on $1 (see wsl-satellite.sh for why ss, not connect).
port_listening() {
  [[ -n "$(ss -ltnH "sport = :$1" 2>/dev/null)" ]]
}

pkill -u "$USER" -x nabu-satellite 2>/dev/null || true
for _ in $(seq 1 20); do
  port_listening "$SAT_PORT" || break
  sleep 0.25
done
if port_listening "$SAT_PORT"; then
  pkill -9 -u "$USER" -x nabu-satellite 2>/dev/null || true
  sleep 0.5
fi

# -audio_buffer_size 50: dshow's default ~500 ms buffer would delay every mic sample
# on the wake AND speech->STT paths (same role as arecord's -F 20000).
# The quotes around the device name push both commands down the satellite's `sh -c`
# path (see satellite/src/audio/mod.rs) — sh exec-optimizes a single simple command,
# so kill/preempt still reaches the player itself.
mic_cmd="'$ffmpeg' -hide_banner -loglevel error -f dshow -audio_buffer_size 50 -i 'audio=$MIC_DEVICE' -ar 16000 -ac 1 -f s16le -"
# ffplay renders via SDL->WASAPI natively. -autoexit makes stdin-EOF -> drain -> exit,
# matching the satellite's finish() contract; nobuffer/low_delay/probesize keep the
# raw-PCM demuxer from adding start latency. adelay pads 150 ms of silence ahead of
# every stream: each play is a fresh WASAPI/RDP session whose first instants can
# randomly glitch — inaudible under a long TTS reply, but a 120-180 ms earcon lives
# entirely inside that window; the pad moves the artifact into silence.
snd_cmd="'$ffplay' -hide_banner -loglevel error -nodisp -autoexit -fflags nobuffer -flags low_delay -probesize 32 -f s16le -ar 22050 -ch_layout mono -af adelay=150:all=1 -i -"

echo "nabu-satellite listening on tcp://0.0.0.0:$SAT_PORT (hub dials in; threshold=$THRESHOLD; Windows-native audio)"
exec env RUST_LOG="${RUST_LOG:-info}" "$bin" \
  --listen "0.0.0.0:$SAT_PORT" \
  --threshold "$THRESHOLD" \
  --mic-command "$mic_cmd" \
  --snd-command "$snd_cmd"
