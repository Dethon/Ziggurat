# 10: Approvals belong to the asker, answered from any of their channels

**What to build:** An approval records the asking principal; only that user's answer counts, on any channel. It shows where the turn asked and is also offered in the person's Telegram private chat and as a WebChat notification; the first answer wins and the others show "answered elsewhere". A fire's approval goes to the schedule's creator. Spec: `../spec.md` ("Approvals").

**Blocked by:** 06, 07

**Status:** ready-for-agent

- [ ] Another person's answer is ignored on every channel (approval service tests)
- [ ] The asker's Telegram answer settles a voice-asked approval, and the WebChat card shows answered elsewhere
- [ ] A fire's approval is offered to the creator only
