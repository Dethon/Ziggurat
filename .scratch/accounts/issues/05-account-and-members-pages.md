# 05: Account and members pages

**What to build:** On the accounts service: a person's page lists their passkeys and signed-in devices, adds a passkey, revokes either (revoking a passkey ends its sessions), shows the link-Telegram code and the voice enrollment command. An owner's members page invites by single-use link (role and display name, seven days), issues a recovery link, changes roles and grants or withdraws "sees all conversations". Spec: `../spec.md` ("Invites and recovery", "Passkeys").

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] Revoking a device ends that session; revoking a passkey ends every session it began (test host)
- [ ] An invite enrolls a member with the named role; a recovery link adds a passkey to an existing member (Playwright)
- [ ] A member can't reach the members page
