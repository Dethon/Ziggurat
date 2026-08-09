# 08 — Sandbox landing

**What to build:** Where an agent has a sandbox, an attachment is not only something the model
can look at but a real file it can work on. As the turn is built, each file is written into the
sandbox under a directory for the conversation and one for the message, keeping the filename the
person used, and the resulting virtual path is named in the message so the model can act on it
without being told a tool exists. Two files with the same name in one conversation both survive,
because the per-message directory removes collisions rather than renaming anything.

An agent with no sandbox is unaffected: its attachments remain model context, which is the whole
feature for that agent. A failed write is logged and the turn proceeds as context-only, because
a broken tool should not cost an answer the model could still give.

The sandbox is the only filesystem where an attachment appears as a file to the model, and the
upload store is deliberately not mounted. See ADR `0021`.

**Blocked by:** 03 — An attachment reaches the model.

**Status:** resolved

- [x] Each attachment is written into the sandbox under a per-conversation, per-message directory, keeping the user's filename.
- [x] The message names the file's virtual path so the model can act on it unprompted.
- [x] Two files with the same name in one conversation both survive.
- [x] An agent with no sandbox still receives the attachment as model context and nothing fails.
- [x] A failed sandbox write leaves the turn intact and the attachment still reaches the model.
- [x] The upload store is not mounted, and attachments are not reachable from any mount other than a sandbox.
- [x] Monitor-level tests cover the sandbox path in the message, the sandbox-less agent, and the failed write.

## Comments

The per-message directory removes collisions between messages, which is what the ticket and
ADR `0021` describe, and the test pins that case: two `photo.png`s sent in two messages of one
conversation both land. It does not remove collisions *within* one message — attaching two files
named `photo.png` to the same message writes one over the other, because the layout the ADR fixes
(`~/uploads/<conversation>/<message-id>/<filename>`) has nothing left to separate them by. Left
as designed rather than renamed, since renaming is what the ADR ruled out; a follow-up would need
a new decision about the layout.
