# 09 — Retention

**What to build:** Files do not accumulate forever. Deleting a topic deletes the files sent in
it, so removing a conversation removes what was in it. A sweep collects everything topic
deletion never reaches: conversations nobody deletes, and files uploaded for a message that was
abandoned before it was sent. The sweep runs on a configured window matching the retention the
conversation history already has.

Files copied into a sandbox belong to the agent from that moment and are not swept.

The sweep window is measured in days while hydration depth is measured in messages, so they
cannot be reconciled exactly — a quiet conversation can have a file swept while its reference is
still within the hydration depth. That is expected, and it is what the placeholder from ticket 07
is for.

**Blocked by:** 01 — Upload store and its endpoints.

**Status:** ready-for-agent

- [ ] Deleting a topic removes that conversation's files from the upload store.
- [ ] A sweep removes files older than the configured retention window.
- [ ] Files uploaded for a message that was never sent are collected by the sweep.
- [ ] Sandbox copies are never swept.
- [ ] The retention window is a setting, defaulting to the same window the conversation history uses.
- [ ] A swept file's reference still reads back as a placeholder rather than an error.
