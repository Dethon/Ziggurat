# 12 — Distribution and autostart

**What to build:** the speech typist becomes something you install by copying one file and uninstall by
deleting it, and that can be there every time you log in without you finding the Startup folder.

A release build script mirroring `satellite/scripts/build-release.sh`, cross-compiling from WSL to
Windows and dropping the executable somewhere the Windows host can reach, with a size-tuned release
profile. One file, no accompanying DLLs.

The tray menu gains a "start with Windows" toggle that writes the current user's Run key. It is off by
default: installing this must change nothing about the machine until it is asked to. The menu also
lists the capture devices it can see, which is how you find the name fragment ticket 11 wants without
guessing at what Windows calls your microphone.

Finally the crate's `CLAUDE.md`, holding the invariants that must not be broken rather than a tour of
the code, in the shape `satellite/CLAUDE.md` set: the one host port and why there is only one; the
build invocation and the crate choices ticket 01 established; that only WAV is ever sent and why; that
the model name has to agree with `STT_MODEL` by hand and that a mismatch is slow rather than broken;
that the capture opens on key-down with no pre-roll and the cue plays after it opens; that no window is
ever created because a focusable window breaks injection; and that the glossary words are dictation,
segment, binding, injection and target window, never chunk, utterance or recording.

Also record the rough edges accepted on purpose: the first 50–200 ms lost to opening the device, and
Whisper capitalising the start of every segment so a mid-sentence cut can read oddly at the join.

**Blocked by:** 07 (the tray menu it extends), 11 (the device list serves its name fragment).

**Status:** ready-for-human

- [x] A release script cross-compiles from WSL and produces one executable with no extra DLLs
- [x] The release profile is tuned for size
- [ ] The tray toggles starting with Windows, off by default, by writing the user's Run key
- [ ] Turning it off removes what turning it on added
- [ ] The tray lists the capture devices it can see
- [x] `speech-typist/CLAUDE.md` records the invariants above, including the accepted rough edges
- [x] `satellite/CLAUDE.md` is untouched and the two crates still share no build

## Comments

2026-08-14 — Done except what needs the machine. `scripts/build-release.sh` cross-compiles from
WSL and prints the imported DLLs, because the invariant worth checking is not the size but that
every import is a Windows system DLL. The release profile is `opt-level = "z"` with fat LTO and
`panic = "abort"`; the result is 1.7 MB.

`speech-typist/CLAUDE.md` holds the invariants and the accepted rough edges. `satellite/CLAUDE.md`
is untouched and the two crates share no build — there is no workspace at the repository root, and
neither crate's manifest mentions the other.

The autostart toggle and the device list are written; nobody has clicked them.
