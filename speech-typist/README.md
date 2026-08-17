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
commented, and tells you the default bindings are F13 (Spanish) and F14 (English). If your
keyboard has neither — most do not — right-click the tray icon, choose **Set binding**, pick the
language, and press the key you actually want.

A `config.toml` sitting **beside the executable** wins over the one in your profile, so the
settings travel if you carry the executable on a stick.

## Settings worth knowing about

- `lemonade.base_url` — the Lemonade host as seen from this desktop. The compose-internal
  `lemonade:13305` means nothing from here.
- `lemonade.model` — the model Lemonade currently has **loaded**, named exactly. Get it wrong and
  every dictation fails with a 409: Lemonade holds one transcription model at a time and the
  deployed one is pinned, so it refuses to swap rather than obliging. Ask the server what it has:
  `curl -s http://ai370:13305/api/v1/health | grep -o '"model_loaded":"[^"]*"'`.
- `audio.device_name` — a case-insensitive fragment of a microphone's name. Empty means the system
  default. The tray's **Microphones** submenu lists what Windows calls the devices it can see.
- `detector.*` — where a dictation is cut into segments. Raise the thresholds in a loud room.
- `gate.*` — what a transcript has to look like to be typed at all. This is what stops the fan and
  the keyboard putting a stock "Thank you." into your document.
- `dictation.mode` — `hold` (the default) holds the key for as long as you speak. `latch` makes
  one press begin the dictation and the next press of the same key end it, with nothing held in
  between. Only the key that began it can end it, and it runs for as long as you want — nothing
  cuts it off, so a latched dictation you walk away from keeps the microphone open until you
  press again. The tray icon shows it recording the whole time. The tray's **Latched dictation**
  tick is the same setting, and writes it back to this file.
- `injection.method` — `keys` types Unicode key events; `clipboard-paste` is the escape hatch for
  applications that mishandle them, and it restores whatever you had copied.
- `[[bindings]]` — a key, the language its words are expected in, and the vocabulary they should
  be spelled by. Two ship: F13 for Spanish and F14 for English. They are all live at once, and
  which key you held is the whole choice — there is no mode. Note that both go to the one loaded
  model, so a Spanish fine-tune will transcribe English poorly whatever `language` says.

## The tray

Four states: idle, recording, transcribing, and an error ring when Lemonade is not answering. The
menu holds per-binding learn mode, the microphone list, a **Latched dictation** tick (press to
start and press again to stop, instead of holding), a **Start with Windows** toggle (off until you
ask for it, and turning it off removes exactly what turning it on added), and quit.

Both ticks are written back to `config.toml` and take effect immediately, so the menu and the file
are two views of one setting rather than two places to set it.

## Building

See `CLAUDE.md`. Short version: `scripts/build-release.sh` from WSL, `cargo test` for the suite.
