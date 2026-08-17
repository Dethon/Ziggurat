# speech-typist

Standalone Rust crate (NOT in the .NET solution, and sharing no build with `satellite/`): a
Windows tray program that turns a held key into typing in whatever window was already in front.
It reaches no agent, sends no message and keeps no history. `README.md` covers building and
running; this file holds the invariants that must not be broken.

Nothing in `Ziggurat.sln` is referenced, built or required. `cargo test` in this directory is the
whole suite and must pass with no .NET project built and no Lemonade reachable.

## Invariants

- **One host port, and only one.** `src/host.rs`'s `Host` trait plus its inward `HostEvent`
  channel is the entire boundary between the core and the outside world. Everything else —
  segmenting, ordering, prompt chaining, the gate, retries, the window guard, the watchdog, the
  tray states — is platform-free and driven in tests through one fake host that records what it
  was asked to do. Do not add a second seam. A Linux implementation would be a second
  implementation of this trait and no other change; none is written, and Wayland forbids both
  global key capture and synthetic input without portals or `/dev/uinput`, so it may never be
  possible there.
- **It posts at Lemonade directly.** `docs/adr/0026-the-speech-typist-talks-to-lemonade-directly.md`
  records why, and what duplication was accepted in exchange. Read it before proposing that this
  call into Ziggurat. The consequence that matters: the speech typist works with the whole rest of
  the stack down, and does not work with Lemonade down, and there is no fallback.
- **Only WAV is ever sent.** The speech typist captures its own audio, so there is no
  sender-supplied media type to distrust, nothing to sniff and no Opus to decode — the three
  problems the .NET side's `AudioContainer` exists to solve are problems this does not have. No
  resampler either: the WAV declares whatever rate the device opened at, exactly as
  `WavAudio.FromPcm` does by taking whatever rate it was handed.
- **The model name must be the one Lemonade has loaded, and a wrong one breaks it outright.**
  `DockerCompose/docker-compose.yml` keeps four sides in lockstep with its `&stt-model` anchor;
  this config is outside compose and beyond the anchor's reach, so it is a fifth that has to be
  repeated by hand. It was assumed while this was written that a mismatch would merely be slow —
  Lemonade lazily pulling what it was asked for — and that is **false**: Lemonade holds one
  transcription model at a time, the deployed one is pinned, and asking for any other name is
  refused with `409 slots_pinned_error` so nothing is typed at all. Measured against the live
  instance on 2026-08-14, which answers `Whisper-Large-v3-Turbo-ES`; compose's
  `${STT_MODEL:-Whisper-Large-v3-Turbo}` fallback is *not* what runs. Ask the server rather than
  assuming: `curl -s http://ai370:13305/api/v1/health | grep -o '"model_loaded":"[^"]*"'`.
- **The base URL is `/v1`, not `/api/v1`.** Lemonade serves both; health lives under `/api/v1`
  and the OpenAI-compatible routes under `/v1`. The wrong one parses fine and 404s only when
  somebody is mid-sentence. A test pins it.
- **The capture opens on key-down with no pre-roll, and the cue plays after it opens.** Unlike the
  satellite, this trades the first 50-200 ms of audio for not holding the device and not showing
  Windows' permanent microphone indicator. The cue ordering is the whole mitigation, which is why
  it means "speak now" rather than "key received". This is the choice most likely to want
  revisiting after real use.
- **No visible window is ever created.** A window that can take focus can break injection into the
  window underneath, which is why the tray is the entire interface and the binary is built for the
  windows subsystem. The one `HWND_MESSAGE` window the tray icon and the hook need is never shown,
  never painted and cannot be focused.
- **The keyboard hook must return promptly.** Windows silently removes a hook that takes too long,
  so the callback locks a mutex, decides, and posts to a channel with `try_send`. It never blocks
  and never awaits. For the same reason the core runs on its own thread: a message loop that stops
  pumping is a hook that stops existing.
- **The binding key is swallowed for the whole time it is held**, key-up included, and its own
  auto-repeat is not a second dictation. Everything else the person types mid-dictation still
  reaches the window — this is a dictation key, not a keyboard blocker.
- **The watchdog is a correctness requirement, not a nicety.** A key-up lost to a remote desktop
  session, fast user switching or a hook that lost its window would otherwise hold the microphone
  for as long as the process lives. It is also the backstop if the event queue ever drops a
  key-up under load, and it carries more weight under `dictation.mode = "latch"`, where nothing
  is held and so nothing physically reminds a person the microphone is open.
- **Latched and held are one dictation with two endings, not two features.** `latch` changes
  which event ends a dictation: the hook is unaware of the mode, the same `Live` state runs
  either way, and only the key that began a dictation can end it. Injection is told no key is
  held while latched, because releasing a modifier the person is not pressing would leave the
  keyboard in a state nobody chose.
- **The window guard is a held-mode protection, and latched words follow the focus.** Held, a
  changed window abandons the rest of the dictation — the key was let go and the person moved
  on, and words in the wrong window are worse than missing words. Latched there is no key whose
  release marks the dictation over: moving between windows is part of dictating hands-free, so
  each transcript is typed into the window in front when it arrives, and a segment joins the
  previous one with a space only when that previous text is in the same window.
- **The gate fails open.** A quality signal that is absent or malformed means no signal and the
  words are typed, for the same reason the .NET client fails open: a shortcoming in the response
  must never silently swallow words that were actually said.
- **The prompt cap is applied here, not left to whisper.** whisper.cpp caps at 224 tokens and
  keeps the *tail*, which would eat the vocabulary — the one part a person actually wrote. So the
  chained text loses its oldest words instead. Same rule as
  `McpChannelVoice/Services/Stt/WhisperPromptBuilder.cs`.
- **Everything the tray changes goes through the core, and is written with `toml_edit`.** Learn
  mode and the latched-dictation tick both raise an event rather than writing settings on the
  host's side, so the core stays the one owner of the config; both edit the file in place,
  because re-serializing would delete the commented defaults the file exists to show. The tray
  never flips its own tick either — it draws what the core last announced, so a change that could
  not be saved does not read as though it had been.
- **There is no logging, on purpose.** The binary has no console subsystem, so a `tracing`
  subscriber would write where nobody can read it — it was carried for a while and dropped, and
  dropping `tracing-subscriber` alone took 400 KB off the executable. Everything a person needs to
  know reaches them through the tray's balloon. Before adding a log, decide where it is going to
  be read.

## Building

```sh
scripts/build-release.sh          # one .exe, ~1.3 MB, cross-compiled from WSL (runnable from anywhere)
cargo test                        # the whole suite, no Windows and no network needed
cargo xwin check --target x86_64-pc-windows-msvc   # type-check the Windows half from WSL
```

Prerequisites: `cargo install cargo-xwin --locked` and `rustup target add x86_64-pc-windows-msvc`.
cargo-xwin downloads the MSVC headers and import libraries on first use and caches them.

`.cargo/config.toml` statically links the CRT. Without it the executable needs `vcruntime140.dll`,
which ships with the VC++ redistributable and is not on a clean machine — and one file that needs
a redistributable is not one file. The release script reads the PE import table for that reason;
every entry in it must be a Windows system DLL.

The same file passes `/ignore:4099`, which is what keeps a clean build quiet. Linking the static
CRT otherwise emits about fifty-five lines of LNK4099 — lld hunting for Microsoft's PDBs for
`libcmt.lib` and `libvcruntime.lib`, which cargo-xwin does not download and which nothing here
would read. It is scoped to that one warning deliberately: `-A linker_messages` would hide a real
link failure just as effectively.

Crates that were established to cross-compile this way: `cpal` (WASAPI capture and device
enumeration), `windows` 0.58 (hook, tray, injection, registry, cue playback), `reqwest` with **no
TLS features at all** (Lemonade is plain HTTP on the LAN, and dropping the TLS stack is most of
why the executable is this small — a config naming an `https://` base URL will fail to connect, by
design). The tray is `Shell_NotifyIconW` directly rather than a tray crate: a message loop already
exists for the hook, the same call carries the balloon notifications, and it is one fewer thing
that has to cross-compile.

## Rough edges accepted on purpose

- The first 50-200 ms of a dictation is lost to opening the device, so speaking instantly clips a
  leading plosive.
- Whisper capitalises the start of every segment it is given, so a sentence cut mid-way can read
  slightly oddly at the join. Prompt chaining is what limits the damage, not a fix for it.
- The energy detector will cut on a long deliberate pause mid-sentence.

## Glossary

`CONTEXT.md` at the repository root is the authority. The words are **dictation**, **segment**,
**binding**, **injection** and **target window**. A segment is never a "chunk", a dictation is
never an "utterance" or a "recording", and injection is never "paste" or "send keys".
