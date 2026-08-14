# 07 — Bindings, languages, and learn mode

**What to build:** one key for Spanish prose, another for English identifiers and commit messages,
and no mode to remember you are in. Plus a way to set either of them by pressing the key you actually
want, so a keyboard without an F13 stops being a problem.

A binding is a key together with the language its transcripts are expected in and the vocabulary they
should be spelled by. Several exist at once and are all live; there is no active binding, no toggle
and no mode. Which key you held decides the language and the vocabulary for that whole dictation.
Config ships one Spanish binding, matching the `"Language": "es"` every other STT caller in this repo
pins.

The tray menu gains a "set binding" submenu listing the existing bindings by their language, so
rebinding the English key cannot overwrite the Spanish one. Choose one, press a key, and that key is
written to the config and live immediately. The keyboard hook is already installed, so learn mode is
reading one event rather than new machinery.

Because F13 is the shipped default and many keyboards cannot produce it, the first run should point
you at learn mode rather than leaving you with a binding you cannot press.

**Blocked by:** 04.

**Status:** resolved

- [x] Several bindings are live at once, each with its own key, language and vocabulary
- [x] The key held decides the language and vocabulary sent for that dictation
- [x] Config ships one Spanish binding by default
- [ ] The tray offers learn mode per binding, identified by its language
- [x] Pressing a key in learn mode writes it to config and takes effect without a restart
- [x] Learn mode cannot bind a key already bound to another binding
- [x] A first run with the default F13 tells you how to rebind it
- [x] Binding resolution and the per-binding language reaching the request are tested without Windows

## Comments

2026-08-14 — Done. Which keys the hook watches and swallows now travels outward through the
port, because the callback has to answer that synchronously and cannot ask the core.

The tray submenu itself is Windows and unverified; everything it drives is not. Learn mode
arrives as one event, and the refusal of a key that already belongs to another binding, the
rebinding taking effect with no restart, and the write back into the config are all tested.
`toml_edit` does that write, so the commented defaults the file exists to show survive it.
