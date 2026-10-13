# nabu-satellite

A single static Rust binary that acts as a drop-in [Wyoming protocol](https://github.com/rhasspy/wyoming)
satellite **server**, replacing the EOL Python [wyoming-satellite](https://github.com/rhasspy/wyoming-satellite)
on Raspberry Pi voice satellites. Wake-word detection ("ok nabu", openWakeWord ONNX models) runs embedded
in-process via [tract](https://github.com/sonos/tract) — no Python, no ONNX Runtime, no shared-library
dependencies on the target device.

## Build

### Prerequisites

- **Rust** via [rustup](https://rustup.rs) — no manual toolchain setup needed:
  `rust-toolchain.toml` auto-installs the pinned 1.97.1 toolchain and the
  `aarch64-unknown-linux-musl` target on the first cargo invocation.
- **Python 3** (for the zig toolchain used by cross-builds):
  `python3 -m pip install --user cargo-zigbuild` — the `ziglang` package (which bundles
  the zig compiler) comes in as its dependency. Make sure `~/.local/bin` is on `PATH`.

### Host (dev)

```sh
cargo build
cargo test --release   # the wake-pipeline spike runs real tract inference; release keeps it fast
```

### Cross build (Raspberry Pi, fully static aarch64-musl)

One command (make sure `~/.local/bin` is on `PATH` per the prerequisites above):

```sh
satellite/scripts/build-release.sh
```

Verified working toolchain:

| Component | Version |
|---|---|
| rustc | 1.97.1 (`rust-toolchain.toml` pins the exact patch — 1.97.0 carries a P-critical LLVM miscompile, [rust#159035](https://github.com/rust-lang/rust/issues/159035) — plus the `aarch64-unknown-linux-musl` target) |
| cargo-zigbuild | 0.22.3 (`python3 -m pip install --user cargo-zigbuild`) |
| zig | 0.16.0, provided by the `ziglang` pip package (`python3 -m ziglang version`) |

**If the build stops at `Failed to find zig`:** cargo-zigbuild locates zig through
`python3 -m ziglang`, which breaks when the system Python moves on from the version pip installed
`ziglang` into (the package then sits under an orphaned `~/.local/lib/python3.X/`). Reinstall it
for the current Python, or point at the binary that is already there:

```sh
CARGO_ZIGBUILD_ZIG_PATH=~/.local/lib/python3.10/site-packages/ziglang/zig satellite/scripts/build-release.sh
```

**Caveat — tract-linalg vs zig cc:** `tract-linalg` 0.23 compiles its SVE f16 C kernels with
`-march=armv8.2-a+sve+fp16`. zig cc rejects GCC's `fp16` extension name
(`error: unknown CPU feature: 'fp16'`) because LLVM/zig spell it `fullfp16`. A plain
`cargo zigbuild --target aarch64-unknown-linux-musl --release` therefore fails in
`tract-linalg`'s build script. The fix is a tiny CC shim that rewrites the feature name and
delegates to the same `zig cc` (what cargo-zigbuild's generated wrapper runs) — checked in at
[`scripts/zigcc-fp16-shim.sh`](scripts/zigcc-fp16-shim.sh). `build-release.sh` wires it up via
`CC_aarch64_unknown_linux_musl` (cc-rs uses the shim for C code; Rust code and linking still
go through cargo-zigbuild's own wrappers).

Output: `target/aarch64-unknown-linux-musl/release/nabu-satellite` —
`ELF 64-bit LSB executable, ARM aarch64, version 1 (SYSV), statically linked, stripped`,
**20,816,880 bytes (19.9 MiB)** (fat LTO, `strip = true`, `-C target-cpu=cortex-a53` with
`-aes,-sha2` subtracted — the Pi's BCM2837/RP3A0 lacks the crypto extensions; the binary is
objdump-verified to contain no AES/SHA instructions. The full tract-onnx inference stack plus
the three `include_bytes!`-embedded wake models — ~2.6 MB of ONNX — account for the growth
over the 1.2 MiB pre-tract skeleton).
The 1.91 → 1.97.1 toolchain bump added 1,028,272 bytes (+5.2%) on top of that, from two
independent causes measured section-by-section: `.text` +799,728 (LLVM 22's AArch64 backend
raised optimization effort — unrolling/inlining — for small in-order cores, which is exactly
what `target-cpu=cortex-a53` selects) and `.eh_frame` +319,672 (Rust 1.92 emits unwind tables
by default under `panic="abort"` on Linux). Only the latter is opt-out-able, via
`-C force-unwind-tables=no` — worth ~1.5% of the binary, not the whole 5.2%.
Execution verified on arm64 via Docker binfmt emulation (no Pi needed):

```sh
docker run --rm --platform linux/arm64 \
    -v "$PWD/target/aarch64-unknown-linux-musl/release:/b" \
    alpine /b/nabu-satellite --listen 0.0.0.0:10700 --no-wake
# -> nabu-satellite listening on 0.0.0.0:10700 (hub dials in)
```

`--no-wake` is required **under qemu only**: qemu-user advertises ARMv8.2 fp16 hwcaps, so
tract-linalg activates f16 assembly kernels that crash under emulation during the boot-time
model load. A real Pi A53 has no fp16 hwcap and auto-selects the Cortex-A53 f32 kernels
(`RUST_LOG=tract_linalg=info` shows `CPU optimisation: CortexA53` at startup).

## Hardware defaults

Compiled-in defaults target a **reSpeaker XVF3800** USB mic array (`plughw:CARD=Array,DEV=0`)
paired with a **HiFiBerry MiniAmp** I2S speaker (`plughw:CARD=sndrpihifiberry,DEV=0`), matching
the live speaker-fran-office unit. Provisioning rewrites both device strings anyway: it
auto-detects the USB capture card by *name* from `arecord -l` (confirm yours with `arecord -L`)
and picks the I2S DAC/amp HAT (`sndrpihifiberry*`) as the speaker when one is present, else the
mic card itself. By-name addressing is immune to ALSA index churn — the old `snd_usb_audio
index=0` pinning collided with the Pi's built-in vc4-hdmi/headphone cards and was removed. No
button by default (`--button-gpio 17` is the reSpeaker 2-Mic HAT's opt-in path; `--button-evdev`
is the other option). The mic is 16 kHz mono native (no
resampling); `plughw` resamples only the 22050 Hz playback.

The default audio commands carry latency-tuned ALSA flags — keep them when overriding devices:

- `arecord … -F 20000` (20 ms capture period): the alsa-utils default is buffer/4 = 125 ms
  periods, which delays every mic sample by up to 125 ms on both the wake and speech→STT path.
- `aplay … --start-delay=100000 -F 50000`: aplay's default start threshold is the full 500 ms
  buffer, so a streamed TTS reply wasn't audible until 500 ms of audio had been
  synthesized+delivered; playback now starts at ~100 ms queued (the 500 ms buffer stays for
  underrun headroom).

## Deploying the office unit (speaker-fran-office)

The one deployed satellite: Pi 4 + XVF3800 + MiniAmp at `dethon@192.168.5.11`, a music unit. Run
from the repo root; it builds, installs the binary and rewrites the unit and its drop-in:

```sh
MUSIC_HUB=192.168.5.45 MUSIC_ROOM=fran_s_office \
THRESHOLD=0.5 WAKE_WINDOW=3 WAKE_MIN_RMS=350 WAKE_QUIET_RMS=200 \
scripts/provision-satellite-rs.sh dethon@192.168.5.11
```

Every variable is load-bearing, because provisioning installs its defaults for whatever is left out:

- Without `MUSIC_HUB` / `MUSIC_ROOM` the unit is torn down to voice-only (no snapclient, no PipeWire).
- Without `THRESHOLD` / `WAKE_WINDOW` wake tuning returns to 0.7 / 2.
- Without `WAKE_MIN_RMS` / `WAKE_QUIET_RMS` the loudness floor and the quiet-room rule are off.
  `WAKE_QUIET_THRESHOLD` / `WAKE_QUIET_WINDOW` are left at their 0.9 / 2 defaults on purpose.
- `MASTER_VOLUME` is omitted, so the master returns to 50%; pass the current level to keep it
  (`ssh dethon@192.168.5.11 XDG_RUNTIME_DIR=/run/user/1000 wpctl get-volume @DEFAULT_AUDIO_SINK@`).
- `TTS_VOLUME` / `ALERT_VOLUME` are at their 100% defaults on this unit.
- `DAC_OVERLAY=hifiberry-dac` is only for a fresh Pi; the overlay is already in its `config.txt`.

It ends in these wake flags on the running process (`pgrep -af nabu-satellite`):
`--threshold 0.5 --wake-window 3 --wake-min-rms 350 --wake-quiet-rms 200 --wake-quiet-threshold 0.9 --wake-quiet-window 2`.

**Binary-only update** — what the 2026-10-14 deploy of the floor and the quiet-room rule actually
ran, because a full provision wipes `nabu-satellite.service.d/` and a temporary mic capture was
living there. It reaches the same wake flags without touching audio routing or volumes:

```sh
satellite/scripts/build-release.sh
scp satellite/target/aarch64-unknown-linux-musl/release/nabu-satellite dethon@192.168.5.11:/tmp/nabu-satellite.new
ssh dethon@192.168.5.11 'sudo cp -p /usr/local/bin/nabu-satellite /usr/local/bin/nabu-satellite.bak.$(date +%Y%m%d) \
  && sudo install -m 755 /tmp/nabu-satellite.new /usr/local/bin/nabu-satellite'
# Wake flags live at the end of the LAST drop-in's ExecStart (pipewire.conf, or whichever sorts after it):
ssh dethon@192.168.5.11 'sudoedit /etc/systemd/system/nabu-satellite.service.d/pipewire.conf'
ssh dethon@192.168.5.11 'sudo systemctl daemon-reload && sudo systemctl restart nabu-satellite \
  && sleep 8 && systemctl is-active nabu-satellite && pgrep -af "nabu-satellite --listen"'
```

Roll back with the `.bak.<date>` binary and a restart. An unknown flag makes the binary exit at
startup, so remove the new flags from the drop-in before restoring an older binary.

## Status LED

The satellite drives the reSpeaker XVF3800's 12-LED WS2812 ring, showing a distinct look for
each phase of a voice interaction:

| Phase | Ring |
|---|---|
| Idle | dark |
| Listening | blue ring with a green direction-of-arrival pointer |
| Thinking | breathing blue |
| Speaking | solid blue |

It is enabled automatically when the array is on USB; `--no-led` opts out. The service needs
write access to the USB device node — provisioning installs a udev rule
(`MODE="0660", GROUP="plugdev"`) and the [systemd unit](deploy/nabu-satellite.service) lists
`plugdev`. Missing hardware is not an error: an absent array, or a host with no USB subsystem
at all, simply runs without an LED.

## Testing on the WSL dev host

Run the native release binary against the real hub (hub config dials `tcp://<host>:10800`):

```bash
cd satellite && RUST_LOG=info ./target/release/nabu-satellite \
  --listen 0.0.0.0:10800 \
  --mic-command 'parecord --raw --rate=16000 --format=s16le --channels=1 | python3 -u -c "
import sys, audioop
r, w = sys.stdin.buffer, sys.stdout.buffer
while b := r.read1(4096):
    w.write(audioop.mul(b, 2, 3.0))
"' \
  --snd-command 'paplay --raw --rate=22050 --format=s16le --channels=1 --latency-msec=50'
```

- **`--latency-msec=50` on paplay is mandatory on WSLg**: the RDP sink's default stream buffer
  adds ~1.6 s of playback latency per stream (measured: a 0.5 s clip takes ~2.1 s to drain by
  default, 0.51 s with the flag). Without it every TTS reply/chime/cue plays ~1.6 s late and the
  hub's follow-up window (whose timing model assumes near-zero sink latency, compensated only by
  `FollowUp:PlaybackTailMs` = 400 ms) is perceptually swallowed by the lag.
- The python pipe applies 3x mic gain (`audioop.mul`, saturating): WSLg's mic bridge arrives
  quiet and the binary has no gain flag. Tune the `3.0` or drop the pipe on a proper mic.
- On a Pi with direct ALSA (`aplay -D plughw:...`) the sink buffer is far smaller; the flag is a
  PulseAudio-ism and does not apply.

## Model licenses

The ONNX models under `models/` (melspectrogram, speech embedding, `ok_nabu` wake classifier)
are **CC-BY-NC-SA 4.0 (NonCommercial)** — fine for personal use, not redistributable in a
commercial product. Sources and details: [`models/LICENSE-models.md`](models/LICENSE-models.md).
