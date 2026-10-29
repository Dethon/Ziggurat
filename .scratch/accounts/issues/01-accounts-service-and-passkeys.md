# 01: The accounts service, passkeys and the operator's first link

**What to build:** A new `Accounts/` host and `accounts` container with its EF Core store on SQLite (tenants, users, credentials, invites, recovery links, sessions; nothing SQLite-specific), migrations at start, and the home tenant seeded. The operator command `accounts mint-link` prints a one-time enrollment link. Enrollment and sign-in pages run the WebAuthn ceremonies (Fido2NetLib, discoverable credentials, no username). A session cookie on the parent domain lasts until revoked. A verify endpoint answers a valid session with the user, tenant and roles headers, a browser without one with a redirect to sign-in, and an API call with a 401. Spec: `../spec.md` ("The accounts service", "Storage", "Passkeys", "Invites and recovery").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A minted link enrolls a passkey once and then refuses; an expired link refuses (test host)
- [ ] Sign-in with a virtual authenticator and no username sets the session; verify answers its headers (Playwright)
- [ ] Verify answers a redirect to a browser and a 401 to an API call without a session
- [ ] The compose service, its volume and its secrets follow the baseline's per-service rules
