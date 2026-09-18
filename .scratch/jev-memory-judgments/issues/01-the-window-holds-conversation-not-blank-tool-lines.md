# 01 — The window holds conversation, not blank tool lines

**What to build:** A defect fix with no Jev in it. Today a tool call and its result reach the extractor as two blank lines, the result labelled `user:`, each taking one of the window's slots — demonstrated 2026-09-18 with a six-message history whose first turn fell out of view. `ExtractionWindow.Build` leaves out tool-role messages and assistant messages that carry no text, and the window size counts what remains. Test-first, starting with the test that shows the blank `user:` line. The rule file's description of the window is brought up to date.

**Blocked by:** None — can start immediately.

**Status:** resolved

- [x] A test with a user turn, a tool-call-only assistant message, a tool result and a text reply first fails showing the blank lines, then passes with neither rendered.
- [x] With a window of six, a history of two text turns then a two-tool turn keeps all of the earlier text turns the six slots allow.
- [x] An assistant message with both text and a tool call keeps its text.
- [x] The `[CURRENT]` / `[context -N]` numbering is unchanged for a window with no tool messages; the marker cross-check test still passes.
- [x] The anchor's meaning is unchanged: it still counts persisted messages, filtered after the cut.
- [x] Spec: `.scratch/jev-memory-judgments/spec.md` § The window.
