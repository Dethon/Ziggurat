# 04: Follow-ups

Spec: `.scratch/app-asks/spec.md` ("Conversations").

**What to build:** Every answer carries a conversation id. Passing it on the next ask continues that
conversation with its history, for the same app and viewer only, within a follow-up window.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] The first ask mints the id and its answer returns it. A follow-up is a turn on the same conversation, and the model sees the earlier exchange (origin and agent seams).
- [ ] A follow-up from another app, another viewer, or an id never minted is refused before any turn, with the unknown-or-expired code.
- [ ] A follow-up after the window, or after an apps-server restart, is refused the same way, so the app starts afresh.
- [ ] The window is an apps-server setting.
