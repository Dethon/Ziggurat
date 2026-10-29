# 02: The accounts service: passkeys and the operator's first link

**What to build:** The operator runs a command inside the new accounts service, gets a one-time link, enrolls a passkey and signs in with no username, and the session check answers for that session. The service stores households, people, credentials, invites, recovery links and sessions in a provider-neutral relational store with the home household seeded, and its sessions last until revoked. Spec: `../spec.md` ("The accounts service", "Passkeys, sessions, invites and recovery", "The session check at the proxy").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A minted link enrolls a passkey once and then refuses; an expired link refuses
- [ ] Sign-in with a virtual authenticator and no username sets a session, and the session check answers the user, tenant and roles headers
- [ ] Without a session, the check redirects a page request to sign-in and answers an API call 401
- [ ] The service runs as its own container under the baseline's per-service rules, its store migrated at start
