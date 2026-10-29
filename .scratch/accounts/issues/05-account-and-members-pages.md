# 05: Account and members pages

**What to build:** A person manages their own passkeys and devices, and an owner manages the household. The account page lists passkeys and signed-in devices, adds a passkey, and revokes either, revoking a passkey ending the sessions it began; it shows the Telegram link code and the voice enrollment command. The members page lets an owner invite by a single-use, week-long link naming role and display name, issue a recovery link, change roles and grant or withdraw "sees all conversations". Spec: `../spec.md` ("Passkeys, sessions, invites and recovery").

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] Revoking a device ends that session; revoking a passkey ends every session it began
- [ ] An invite enrolls a person with the named role and display name; a used or expired invite refuses
- [ ] A recovery link adds a passkey to an existing person
- [ ] A member is refused the members page
- [ ] Everyone in the household can see who holds "sees all conversations"
