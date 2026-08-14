# Speech typist

Status: implemented — the Windows half awaits a real machine (see the issues)

## Problem Statement

I type all day, and a lot of what I type is prose I could have said out loud in a third of the
time — commit messages, chat replies, notes, prompts, issue bodies. Ziggurat can already turn my
voice into words: Lemonade runs Whisper on the same network I work on, and both chat channels and
the voice hub dictate through it every day. But every one of those paths ends in a conversation
with the agent. None of them can put words into the window I happen to be working in.

So when I want to dictate into anything else — an editor, a terminal, a browser text box, a
Telegram desktop window that is not talking to my own bot — I have no option but the keyboard.
Windows' own dictation exists, sends my audio to Microsoft, and does not know any of the
vocabulary I actually use.

## Solution

A speech typist: a small program that sits in the system tray on my Windows machine. I hold a
key, I talk, and the words appear in whatever window I was already using, as though I had typed
them. Letting go of the key ends it. Right-clicking the tray icon closes it.

It reaches Lemonade directly, so it works whenever Whisper is up, whether or not the rest of the
stack is running. It ships as one executable file with nothing to install. It costs nothing while
I am not using it: no microphone held open, no model loaded, no window on screen.

Words arrive while I am still talking. It cuts a dictation into segments wherever I pause, sends
each one as I go, and types each transcript the moment it comes back, so a long dictation reads
onto the screen at roughly the speed I speak rather than all at once when I finish.

## User Stories

1. As a person at my keyboard, I want to hold one key and speak, so that I get text without
   moving my hands to the mouse or opening another window.
2. As a person at my keyboard, I want the words to arrive in the window I was already using, so
   that I never have to copy them from somewhere else.
3. As a person at my keyboard, I want the words to be indistinguishable from typing, so that the
   application I am dictating into behaves exactly as it always does — autocomplete, undo, input
   validation and all.
4. As a person dictating a long paragraph, I want each phrase to appear shortly after I say it, so
   that I can tell it is working and correct my course mid-thought.
5. As a person dictating, I want a pause in my speech to be a natural place for text to arrive, so
   that words never appear in the middle of a phrase I was still finishing.
6. As a person who monologues without pausing, I want my speech to still be transcribed, so that
   speaking for a minute straight does not silently lose everything after the first half-minute.
7. As a person dictating, I want a phrase that continues across a pause to be understood as one
   thought, so that the second half is not transcribed as though it began from nothing.
8. As a person who uses project-specific words, I want to give the speech typist a vocabulary, so
   that names, jargon and technical terms are spelled the way I spell them.
9. As a person who works in two languages, I want a separate key for each, so that dictating
   Spanish prose and dictating English identifiers are two key presses and not a mode I have to
   remember I am in.
10. As a person who dictates in Spanish, I want Spanish to be the default, so that the speech
    typist matches everything else in this repo without configuration.
11. As a person who presses the key and says nothing, I want nothing to happen, so that a
    mis-press costs me nothing and leaves no trace.
12. As a person in a room with a fan and a mechanical keyboard, I want near-silence not to be
    transcribed, so that Whisper's stock hallucinations never get typed into my document.
13. As a person dictating, I want a quality signal Whisper failed to send to be treated as
    permission rather than refusal, so that a shortcoming in the response never silently swallows
    words I actually said.
14. As a person who let go of the key and walked away, I want the rest of my words dropped rather
    than typed into whatever I clicked next, so that a dictation can never leak into the wrong
    application.
15. As a person whose network blipped mid-sentence, I want the rest of the sentence to keep
    working, so that one timeout does not cost me the whole dictation.
16. As a person whose Lemonade is down, I want to be told once, so that I stop talking instead of
    dictating an entire paragraph into nothing.
17. As a person at my keyboard, I want to know the microphone is live before I start speaking, so
    that I do not lose my first syllable to a device that had not finished opening.
18. As a person at my keyboard, I want to glance at the tray and know whether it is recording,
    transcribing, idle or broken, so that I can tell a slow Whisper from a dead one.
19. As a person dictating into a full-screen application, I want no window to ever appear, so that
    nothing steals focus from what I am typing into.
20. As a person who finds the cue sounds annoying, I want to turn them off, so that the speech
    typist can be silent without giving up the tray icon.
21. As a person whose keyboard has no F13 key, I want to press the key I actually want and have it
    become the binding, so that I never have to look up a virtual-key name.
22. As a person with several bindings, I want to choose which one I am rebinding, so that setting
    the English key does not overwrite the Spanish one.
23. As a person at my keyboard, I want the binding key swallowed while I hold it, so that holding
    it does not also toggle Caps Lock, type an F13 into my editor, or trigger the application's own
    shortcut.
24. As a person with more than one microphone, I want to name the one to use, so that plugging in a
    webcam does not silently change which one I am dictating through.
25. As a person with one microphone, I want it to just work, so that I do not have to configure
    anything to get started.
26. As a person distributing this to my own machines, I want a single executable file, so that
    installing it is a copy and uninstalling it is a delete.
27. As a person who carries the executable on a stick, I want a config file next to it to win, so
    that the settings travel with it.
28. As a person who installed it properly, I want the config to live in my user profile, so that it
    survives moving the executable.
29. As a person running it for the first time, I want a config file written for me with the
    defaults and comments, so that I can see what is configurable without reading the source.
30. As a person who wants it always available, I want to turn on starting with Windows from the
    tray, so that I do not have to find the Startup folder.
31. As a person who does not want it always running, I want that off by default, so that installing
    it changes nothing about my machine until I ask.
32. As a person who is done, I want to close it from the tray, so that it is gone until I start it
    again.
33. As a person whose key-up was never delivered — remote desktop, fast user switching, a lost
    focus — I want the dictation to end by itself, so that the microphone is never held open
    indefinitely.
34. As a person who presses a second binding while one is live, I want the second ignored, so that
    two languages never interleave into the same window.
35. As a person reviewing this work, I want the whole thing to work on my machine before it ever
    works on anyone else's, so that no effort is spent on Linux or macOS support I have not asked
    for.
36. As a developer of this crate, I want the segmenting, ordering, gating and retry behaviour
    covered by tests that run in WSL, so that only the thin Windows layer needs a real machine to
    verify.
37. As a developer of this crate, I want the Lemonade request shape pinned by a test, so that the
    contract duplicated into Rust cannot drift from the one the .NET side already speaks.
38. As a developer of this crate, I want the crate to build and test with no part of the .NET
    solution present, so that it stays as independent as ADR 0026 says it is.

## Implementation Decisions

**Placement and toolchain.** A new top-level crate at `speech-typist/`, outside `Ziggurat.sln`,
with its own `Cargo.toml`, `Cargo.lock`, `rust-toolchain.toml` and `CLAUDE.md`. It follows the
`satellite/` precedent and shares no build with it — no Cargo workspace, because satellite is
aarch64-musl, Linux-only, pins its own toolchain and carries `panic = "abort"`. Cross-compiled
from WSL with `cargo-xwin` to `x86_64-pc-windows-msvc`, producing one executable with no
accompanying DLLs. A release profile targeting size, and a `scripts/build-release.sh` mirroring
satellite's.

**Windows only, one port trait.** The implementation is Windows-only. Everything outside the
core sits behind a single **host port**: a trait the core holds, with a real Windows
implementation and one fake used by every test. It carries the outward operations — inject a
transcript, report the identity of the window currently in front, set the tray state, play a cue,
transcribe a stretch of audio — and nothing else. Inward events (binding pressed, binding
released, a microphone frame, a timer elapsing) arrive on a channel that the real host fills from
the keyboard hook and the capture device, and that a test fills directly. A future Linux
implementation is a second implementation of this one trait and no other change; none is written
now, and Wayland forbids both global key capture and synthetic input without portals or
`/dev/uinput`, so it may never be possible there.

**Key capture.** A low-level keyboard hook, because `RegisterHotKey` reports only key-down and
therefore cannot express holding. The binding key is suppressed for the whole time it is held, so
it neither reaches the window in front nor toggles its own state. Default binding is F13, chosen
because no application uses it; because many keyboards cannot produce it, the tray offers a learn
mode — pick a binding from a submenu, press a key, and that key is written to the config.

**Bindings.** A binding is a key together with the language its transcripts are expected in and
the vocabulary they should be spelled by. Several exist at once; there is no active binding and no
mode. Config ships one Spanish binding, matching the `"Language": "es"` every other STT caller in
this repo pins. Pressing a second binding while a dictation is live is ignored.

_Changed 2026-08-14 at the author's request._ Config now ships **two** bindings out of the box —
F13 for Spanish and F14 for English — rather than the one this asked for. User story 9 wanted a
key per language, and shipping only the Spanish half left the English key as something every
install had to add by hand.

**Microphone lifetime.** The capture device is opened when the binding goes down and closed when
it comes up. There is no pre-roll ring, unlike the satellite: this is a deliberate trade of the
first 50–200 ms of audio for not holding the device and not showing Windows' permanent microphone
indicator. The start cue is played **after** the device is open, so it means "speak now" rather
than "key received". Audio is downmixed to mono and kept at the device's native sample rate, then
wrapped in a RIFF header — no resampler, matching what `WavAudio.FromPcm` on the .NET side already
does by taking whatever rate it was handed.

**Segmenting.** An energy detector with hysteresis over the incoming frames. A segment is cut
after roughly 400 ms below the speech threshold, with roughly 200 ms of padding retained either
side of the cut so a leading plosive is not clipped. If no pause has appeared for 20 s, a cut is
forced at the lowest-energy point in the preceding second, keeping every segment inside Whisper's
30 s window. Thresholds and windows are config, not constants. The detector is a pure unit behind
its own small interface so it can be replaced by a model-based one later without touching the
core.

**Transcription.** A direct POST to Lemonade's OpenAI-compatible `/audio/transcriptions` route.
One request in flight at a time, strictly in segment order — which is what makes prompt chaining
possible and removes any need for a reorder buffer. `response_format` is always `verbose_json`.
The `model` field defaults to the model the container is warmed with and must be kept in agreement
with `STT_MODEL` by hand, because the speech typist's config is outside compose and beyond the
reach of its `&stt-model` anchor. The multipart part is named with a `.wav` extension, because
whisper-server refuses a part it cannot name. Per-segment request timeout of 30 s. Only WAV is ever
sent: the speech typist captures its own audio, so there is no sender-supplied media type to
distrust, nothing to sniff and no Opus to decode.

**Prompt.** Each request carries the binding's static vocabulary string followed by the tail of the
previous segment's transcript, truncated to stay near Whisper's 224-token prompt limit (bounded by
characters as a proxy). The first segment of a dictation carries the vocabulary alone.

**Hallucination gate.** `verbose_json` returns `no_speech_prob` and `avg_logprob`, duration-weighted
across the body's segments the same way the .NET client weights them. A transcript above the
`no_speech_prob` threshold (default ~0.6) or below the `avg_logprob` threshold (default ~-1.0) is
dropped rather than injected. A signal that is absent or malformed means no signal, and the
transcript is injected — fail open, for the same reason the .NET side fails open. Both thresholds
are config.

**Injection and the target window.** Synthetic Unicode key events by default, batched into as few
calls as possible, with a config switch to clipboard-paste for applications that mishandle
synthetic input. The identity of the window in front is captured when the binding goes down. Before
each injection the current window is compared against it; if it differs, the remaining segments of
that dictation are discarded and the person is told once. Because segments are injected while the
binding is still held, injection releases any modifier the binding itself holds down for the
duration of the call and restores it afterwards — the default binding has no modifier precisely so
that this path is rarely exercised.

**Joining.** Each transcript is trimmed of leading and trailing whitespace. Every segment after the
first in a dictation is prefixed with a single space. Nothing else is rewritten: Whisper already
punctuates and capitalises, and a rewrite layer would fight it.

**Failure handling.** A failed request is retried once, then that segment is dropped and the
following segments proceed. The tray moves to its error state and one notification is raised per
dictation, not per segment; the error state clears on the next transcript that arrives.

**Watchdog.** If a key-up is never delivered — remote desktop, fast user switching, a hook that
lost its window — the dictation ends by itself after a bounded time and the capture device is
closed. This is a correctness requirement, not a nicety: without it a lost key-up holds the
microphone for as long as the process lives.

**Tray.** Four states — idle, recording, transcribing, error — plus a menu with the per-binding
learn mode, a "start with Windows" toggle writing the current user's Run key (off by default), a
device list to help find a microphone name, and quit. No window is ever created, because a window
that can take focus can break injection into the window underneath.

**Config.** TOML. A file beside the executable wins; otherwise the one under the current user's
application-data directory, written with commented defaults on first run. It holds the Lemonade
base URL (defaulting to the Lemonade host rather than the compose-internal name, which means
nothing from a desktop), the model, the request timeout, the microphone name override, the
injection method, the detector thresholds and windows, the gate thresholds, and the list of
bindings with their key, language and vocabulary.

## Testing Decisions

Red-green-refactor throughout, per the project's `CLAUDE.md`: a failing test first, watched to
fail, then the implementation.

A good test here asserts what the speech typist did, never how. For this feature that means: which
transcripts were injected, in what order, with what joining; which were dropped; what the tray was
asked to show; what was actually sent to Lemonade. It never reaches inside the detector to check a
counter, never asserts on a log line, and never asserts that a particular function was called.
Tests are named after the behaviour they pin, and where a threshold exists because of a real
observation, the test says so in a comment — `satellite/src/audio/room.rs` is the model for both,
including feeding synthetic PCM to a pure unit rather than a fixture where synthetic audio is
enough.

**One seam: the host port.** Every test that covers the core drives it through a single fake host —
push binding-down, a scripted sequence of frames, binding-up, and assert on the injections the fake
recorded. This is what makes the interesting behaviour testable in WSL with no Windows, no
microphone and no network:

- a pause producing a cut, and the padding either side of it being retained
- a dictation with no speech in it injecting nothing at all and reporting no error
- a monologue with no pause being force-cut inside Whisper's window, at a low-energy point
- segments being injected strictly in order, each after the first prefixed with one space
- the second segment's request carrying the first's transcript in its prompt, and the first
  carrying only the vocabulary
- a transcript above the `no_speech_prob` threshold being dropped, and one with the signal absent
  being injected
- a failed request retried once, dropped, and the following segments still injected
- one notification per dictation on repeated failures, and the error state clearing on the next
  success
- the window in front having changed discarding the remaining segments and injecting nothing
- a second binding pressed during a live dictation being ignored
- a key-up that never arrives ending the dictation and closing the capture

**The Lemonade contract, over loopback.** A tiny HTTP server bound to localhost stands in for
Lemonade, so the real client is exercised end to end: the multipart shape, the part's filename and
extension, the `model`, `language`, `prompt` and `response_format` fields, and the parsing of
`verbose_json` including the duration-weighted quality signals and the degradation to no signals
when a body carries no segments. This is the test that protects the duplication ADR 0026 knowingly
accepted, so it asserts on the bytes actually sent rather than on an intermediate structure. It
also covers a timeout and a 500 surfacing as the errors the retry path expects.

**Pure units, no seam needed.** The energy detector fed synthetic PCM; the joining rule; the prompt
composition and its truncation; the config precedence between the file beside the executable and
the one in the user profile, and the defaults written on first run.

**Verified by hand on Windows, not by test.** The keyboard hook and its suppression, the tray icon
and its menu, `SendInput` reaching a real application, clipboard paste, the window-identity call,
the Run-key toggle, and the capture device itself. These are the reason the port trait exists: they
stay thin enough to read, and there is nothing in them worth faking a Windows API to test.

**No CI.** This repo has no workflows. `cargo test` in `speech-typist/` is the whole suite, and it
must pass with no part of the .NET solution built and no Lemonade reachable.

## Out of Scope

- **Spoken commands.** No "new line", no "delete that", no phrase-to-keystroke table. Text in,
  keystrokes out. Whisper already punctuates from prosody, and deciding when "new line" is an
  instruction rather than words is a genuinely hard problem.
- **Linux and macOS.** The port trait is where they would go. No implementation, no
  conditional compilation for them, no speculative abstraction beyond the one trait.
- **Latched dictation.** The chat client can carry a dictation on with nothing held down; the
  speech typist cannot. Holding the key is the whole interaction.
- **Streaming or partial transcripts.** A segment is transcribed once, whole. No interim results,
  no revision of text already injected.
- **Any local model.** Nothing is bundled and there is no offline fallback. With Lemonade
  unreachable, the speech typist does not work, and says so.
- **Rewriting what Whisper returns.** No capitalisation fixing, no punctuation insertion, no
  profanity filter, no formatting of numbers.
- **Reaching the agent.** Nothing the speech typist produces is a message, nothing enters a
  conversation, nothing is remembered after the words are out.
- **Per-application behaviour.** No detecting which application is in front and changing the
  injection method, language or vocabulary for it.
- **An installer.** No MSI, no Inno Setup, no Start menu entry, no elevation. Copy the executable.
- **Resampling and non-WAV containers.** No local resampler, no Opus, no container sniffing.
- **Audio retention.** Audio exists until its words are out of it and is then gone. Nothing is
  written to disk, and there is no debug mode that keeps it.

## Further Notes

**Do the toolchain spike first.** `cargo-xwin` plus a WASAPI capture crate plus the Windows API
bindings plus a tray on an MSVC cross-build from WSL is unproven in this repo. Before any structure
is committed to, build a throwaway that puts an icon in the tray, installs the hook, opens the
capture device, and posts one WAV at Lemonade. If any of those four cannot cross-compile, that
changes the toolchain decision, and it is much cheaper to learn now.

**Known rough edges, accepted deliberately.** Opening the device on key-down loses the first
50–200 ms, so speaking instantly clips a leading plosive; the cue-after-open ordering is the whole
mitigation, and this is the choice most likely to want revisiting after real use. Whisper
capitalises the start of every segment it is given, so a sentence cut mid-way can read slightly
oddly at the join. The energy detector will cut on a long deliberate pause mid-sentence; prompt
chaining is what limits the damage.

**Model name agreement.** `DockerCompose/docker-compose.yml` keeps four sides in lockstep with its
`&stt-model` anchor and explains why. The speech typist is a fifth, and it is outside compose.

_Corrected 2026-08-14 against the running instance._ This assumed a mismatch was not an error —
"Lemonade lazily pulls whatever it was asked for" — and that the symptom was a slow first
dictation. It is not. Lemonade holds one transcription model at a time and the deployed one is
pinned, so a request naming a different model is refused with `409 slots_pinned_error` and nothing
is typed. The name must match what the server has loaded, which prod reports as
`Whisper-Large-v3-Turbo-ES` and not compose's `Whisper-Large-v3-Turbo` fallback.

**Vocabulary.** `CONTEXT.md`'s `## Speech typing` section defines speech typist, binding, segment,
injection and target window, and its `## Dictation` section already defines dictation and
transcript. Those are the words to use in code and in tests. In particular a segment is never a
"chunk", and a dictation is never an "utterance" or a "recording".

**Decision record.** `docs/adr/0026-the-speech-typist-talks-to-lemonade-directly.md` records why
this posts at Lemonade rather than routing through the .NET stack, and what duplication was
accepted in exchange. Read it before proposing that the Rust client call into Ziggurat.
