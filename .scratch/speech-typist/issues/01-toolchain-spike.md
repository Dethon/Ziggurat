# 01 — Toolchain spike: prove cargo-xwin can build all four pieces

**What to build:** a throwaway binary, cross-compiled from WSL to Windows, that proves the four
things the speech typist cannot exist without can all be built this way at once. Run it on the
Windows host: an icon appears in the tray, holding a key is observed and swallowed, the microphone
opens and delivers frames, and one WAV reaches Lemonade and comes back as words.

Nothing here is kept. The point is to learn, before any structure is committed to, whether
`cargo-xwin` targeting `x86_64-pc-windows-msvc` can link a WASAPI capture crate, the Windows API
bindings, a tray crate and an HTTP client together into one dependency-free executable. If any of
the four cannot cross-compile, that changes the toolchain decision — building natively on Windows,
or falling back to the GNU ABI — and it is far cheaper to learn now than after ticket 03.

Write down what was learned: the crates that worked, the versions, the linker flags, the
`cargo-xwin` invocation, the size of the resulting executable, and anything that had to be fought.
Those notes are what ticket 12 turns into the crate's `CLAUDE.md`. Delete the spike itself.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] A single executable cross-compiles from WSL to `x86_64-pc-windows-msvc` via `cargo-xwin`
- [ ] It runs on the Windows host with no additional DLLs present alongside it
- [ ] An icon appears in the system tray and the process can be quit from it
- [ ] A key held down is observed as down-then-up and does not reach the window in front
- [ ] The default capture device opens and delivers frames of PCM
- [ ] One WAV posted at Lemonade's `/audio/transcriptions` returns a transcript
- [ ] The working crate choices, versions, build invocation and executable size are written down
- [ ] The spike is deleted; nothing from it is left in the repository
