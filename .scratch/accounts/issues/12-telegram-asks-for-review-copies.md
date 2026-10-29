# 12: Telegram asks for review copies

**What to build:** A person's Telegram conversations sit among their topics in the chat client. Telegram asks for a review copy on every turn; a private chat's copy belongs to the person, a group's is shared. Spec: `../spec.md` ("Channels and the principal", Telegram; "Topics").

**Blocked by:** 07, 11

**Status:** ready-for-agent

- [ ] A private-chat turn produces a review-copy topic owned by the sender
- [ ] A group turn produces a shared review-copy topic
- [ ] The review copy carries the turn's files as other review copies do
