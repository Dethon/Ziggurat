# 03 — The Windows host: hold a key, get text

**What to build:** the real implementation of the host port, and with it the first demo of the whole
feature. Hold the binding key anywhere in Windows, speak, let go, and the words appear in the window
you were already using. An icon sits in the tray and the program can be closed from it.

The keyboard is read with a low-level hook, not `RegisterHotKey`, because that reports only key-down
and therefore cannot express holding. The binding key is swallowed for the whole time it is held, so
holding it does not also type into the window in front, toggle its own state, or fire the
application's own shortcut.

The capture device opens when the binding goes down and closes when it comes up. There is
deliberately no pre-roll ring — unlike the satellite, this trades the first 50–200 ms of audio for
not holding the device and not showing Windows' permanent microphone indicator. The start cue plays
**after** the device is open, so it means "speak now" rather than "key received". Frames are
downmixed to mono and kept at the device's native sample rate, then wrapped in a RIFF header; there
is no resampler, matching what the .NET side already does by taking whatever rate it was handed.

Injection is synthetic Unicode key events, batched into as few calls as possible. The window in
front is reported by the host so the core can identify it; acting on a change is ticket 10.

No window is ever created. A window that can take focus can break injection into the window
underneath, and that is the reason the tray is the entire interface.

Everything in this ticket is verified by hand on the Windows host. That is what the port exists for:
these implementations stay thin enough to read, and there is nothing in them worth faking a Windows
API to test.

**Blocked by:** 01 (the toolchain must be known to work), 02 (the port must exist to implement).

**Status:** ready-for-agent

- [ ] Holding the binding key anywhere in Windows starts a dictation; releasing it ends one
- [ ] The binding key never reaches the window in front and never toggles its own state
- [ ] The transcript arrives in the window that was in front, indistinguishable from typing
- [ ] The capture device is open only while the binding is held
- [ ] The start cue plays after the device is open, and cues can be turned off
- [ ] A tray icon is present, shows idle and recording distinctly, and quits the program
- [ ] No window is created at any point
- [ ] The whole path works with nothing from `Ziggurat.sln` running, only Lemonade
- [ ] The executable is a single file with no accompanying DLLs
