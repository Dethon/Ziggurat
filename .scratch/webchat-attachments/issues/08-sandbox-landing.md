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

**Status:** ready-for-agent

- [ ] Each attachment is written into the sandbox under a per-conversation, per-message directory, keeping the user's filename.
- [ ] The message names the file's virtual path so the model can act on it unprompted.
- [ ] Two files with the same name in one conversation both survive.
- [ ] An agent with no sandbox still receives the attachment as model context and nothing fails.
- [ ] A failed sandbox write leaves the turn intact and the attachment still reaches the model.
- [ ] The upload store is not mounted, and attachments are not reachable from any mount other than a sandbox.
- [ ] Monitor-level tests cover the sandbox path in the message, the sandbox-less agent, and the failed write.
