# 01: Prefactor: a channel server's waited turns, upload store and follow-ups are shared

Spec: `.scratch/app-asks/spec.md` ("Further Notes"; "Files"; "Conversations").

**What to build:** Sugary's channel already has the pieces a second channel server needs:
- waiting on the reply to a turn it sent, by turn key, with timeout and cancel;
- an upload store, with its sweeper and the `fetch_attachment` tool that serves it;
- binding a conversation it opened to who may continue it, within a follow-up window.

Move these into the shared hosting library so the apps server can use the same code. Sugary behaves
exactly as before.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] The shared pieces have no Sugary vocabulary: no meals, estimates or Sugary settings names.
- [ ] Sugary uses them; every Sugary channel, estimate, clip and upload test stays green unchanged.
- [ ] The follow-up binding is keyed by an opaque owner, so the apps server can key it by app and viewer.
- [ ] The upload store keys its references by household, so no other household's reference is ever served.
