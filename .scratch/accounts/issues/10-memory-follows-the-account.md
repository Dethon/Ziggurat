# 10: Memory follows the account

**What to build:** A person has one memory across Telegram, the chat client and voice. Memory entries and the personality profile are keyed by the principal's user id; recall and extraction read it and never the sender's display name; a turn with no user, such as a voice guest, recalls and extracts nothing. The memory rules follow. Spec: `../spec.md` ("Keyed by user id", Memory).

**Blocked by:** 06, 08

**Status:** ready-for-agent

- [ ] A fact told on Telegram is recalled on the chat client by the same person and not by another
- [ ] A guest turn recalls nothing and stores nothing
- [ ] Dreaming still visits every user's memories
