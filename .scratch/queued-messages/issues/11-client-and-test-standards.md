# 11: The page's queue code and the queue E2Es read as the rest of the repo does

**What to build:** No behaviour change except the chip's icon. Clear the review's client and test findings.

**Blocked by:** 09.

**Status:** ready-for-agent

- [ ] A queue chip draws its file count with a stroked paperclip, so one chip never mixes the filled and stroked families (`blazor-client-ui.md`).
- [ ] Words given back to the composer reach it by an action of the composer's own, not by riding dictation's `PendingTranscript`.
- [ ] `QueueEffect` and `ReturnedMessagesEffect` share one catch-up trigger (topic opened, live again); `StreamingService`'s two send paths build their outgoing message in one place.
- [ ] A topic open reads the queue once, not twice.
- [ ] `McpAgent` asks whether options are the group's in one place.
- [ ] The three queue E2E classes share their helpers (send, open a topic past an overlay, user message locator, idle).
- [ ] An E2E pins a second *person* seeing another member's queued chip by name with no Edit or Remove (ticket 04 covered only a second device).
- [ ] New comments in the touched client files say why, never what.
- [ ] Client tests green under two concurrent processes; the queue E2E collection green.
