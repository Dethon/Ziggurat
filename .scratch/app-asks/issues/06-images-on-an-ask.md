# 06: Images on an ask

Spec: `.scratch/app-asks/spec.md` ("Files").

**What to build:** `apps.ask(text, { files })` sends images from the page, from a camera or a picker.
The apps server stores them in its upload store and names them on the message. The agent fetches them
through `fetch_attachment` on the apps channel, for that household. A follow-up's earlier images come
back from history.

**Blocked by:** 02, 04

**Status:** ready-for-agent

- [ ] Images arrive on the message as references, and the model is sent them (origin and agent seams).
- [ ] Count, size and type limits are apps-server settings. Anything over them is refused before any turn.
- [ ] A reference is served only for the household it was stored for, and never fetched from another household's server.
- [ ] A follow-up carries no files and the model still sees the first ask's images.
- [ ] The sweeper removes uploads after the retention.
- [ ] The volume and settings are wired, and conformance tests updated.
