# 05: The conversation context carries the turn's origin kind

**What to build:** A server answering one of a turn's calls can tell whether the turn is a person's message or a fire (schedule, watch, and later the login wake). The conversation context sent in a call's `_meta` gains the turn's origin kind, absent for a person's own message. Nothing reads it yet beyond tests; ticket 06 refuses a handoff on a fire's turn with it. Spec: `../spec.md` ("Handoff").

**Blocked by:** None (can start immediately)

**Status:** done

- [x] A scheduled fire's turn carries origin kind `Schedule` in every call's conversation context; a watch fire's carries `Watch`
- [x] A person's message carries none
- [x] Older senders and receivers without the field keep working (the field is optional)
- [x] Covered where the conversation context is built and where it is lifted into `_meta`
