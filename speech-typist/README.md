# speech typist

Hold a key, talk, and the words appear in whatever window you were already using, as though you
had typed them. Letting go ends it. Right-clicking the tray icon closes it.

It reaches Lemonade directly, so it works whenever Whisper is up, whether or not the rest of the
stack is running. It ships as one executable with nothing to install, and costs nothing while you
are not using it: no microphone held open, no model loaded, no window on screen.

Words arrive while you are still talking. A dictation is cut into segments wherever you pause,
each is sent as you go, and each transcript is typed the moment it comes back.

## Installing

Copy `speech-typist.exe` anywhere and run it. Uninstalling is deleting it.

On first run it writes a `config.toml` under `%APPDATA%\speech-typist\` with every setting
commented, and tells you the default binding is F13. If your keyboard has no F13 — most do not —
right-click the tray icon, choose **Set binding**, pick the language, and press the key you
actually want.

A `config.toml` sitting **beside the executable** wins over the one in your profile, so the
settings travel if you carry the executable on a stick.

## Settings worth knowing about

- `lemonade.base_url` — the Lemonade host as seen from this desktop. The compose-internal
  `lemonade:13305` means nothing from here.
- `lemonade.model` — keep it in agreement with `STT_MODEL` by hand. A mismatch is not an error;
  the symptom is a slow first dictation while Lemonade pulls what you asked for.
- `audio.device_name` — a case-insensitive fragment of a microphone's name. Empty means the system
  default. The tray's **Microphones** submenu lists what Windows calls the devices it can see.
- `detector.*` — where a dictation is cut into segments. Raise the thresholds in a loud room.
- `gate.*` — what a transcript has to look like to be typed at all. This is what stops the fan and
  the keyboard putting a stock "Thank you." into your document.
- `injection.method` — `keys` types Unicode key events; `clipboard-paste` is the escape hatch for
  applications that mishandle them, and it restores whatever you had copied.
- `[[bindings]]` — a key, the language its words are expected in, and the vocabulary they should
  be spelled by. List several; they are all live at once, and which key you held is the whole
  choice.

## The tray

Four states: idle, recording, transcribing, and an error ring when Lemonade is not answering. The
menu holds per-binding learn mode, the microphone list, a **Start with Windows** toggle (off until
you ask for it, and turning it off removes exactly what turning it on added), and quit.

## Building

See `CLAUDE.md`. Short version: `scripts/build-release.sh` from WSL, `cargo test` for the suite.
