# 04: Members

**What to build:** Owners invite a person by passkey link or Telegram code, set a member's role and sees-all, disable and re-enable a member, and mint a member's recovery link. Disabling revokes their sessions, stops their links and Telegram and voice identities resolving, and stops delegations naming them from acting; their data stays. Spec: `../spec.md` ("Members").

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] An invited person joins the household by passkey or Telegram
- [ ] A disabled member cannot sign in, is not recognised on Telegram or voice, and their schedules fire as nobody
- [ ] Re-enabling restores all of it, with their memories and topics intact
- [ ] Role and sees-all changes take effect on the member's next turn
