# 02: The accounts service: passkeys and the operator's first link

**What to build:** The operator runs a command inside the new accounts service, gets a one-time link, enrolls a passkey and signs in with no username, and the session check answers for that session. The service stores households, people, credentials, invites, recovery links and sessions in a provider-neutral relational store with the home household seeded, and its sessions last until revoked. Spec: `../spec.md` ("The accounts service", "Passkeys, sessions, invites and recovery", "The session check at the proxy").

**Blocked by:** None (can start immediately)

**Status:** done

- [x] A minted link enrolls a passkey once and then refuses; an expired link refuses
- [x] Sign-in with a virtual authenticator and no username sets a session, and the session check answers the user, tenant and roles headers
- [x] Without a session, the check redirects a page request to sign-in and answers an API call 401
- [x] The service runs as its own container under the baseline's per-service rules, its store migrated at start

Notes for later tickets (`.claude/rules/accounts.md` has the detail):

- Session issuance is `SessionStore.IssueAsync`; `docker exec accounts dotnet Accounts.dll mint-session --user <id>` prints a `ziggurat_session` cookie value, and `mint-link` prints the user and household ids. Revoking a passkey (`PasskeyStore.RevokeAsync`) ends the sessions it began; ticket 05 builds the pages on it.
- Invites are modelled (`LinkKind.Invite`, display name and role) but nothing mints or redeems one yet: ticket 05.
- The session check accepts `?role=operator` (403 for a signed-in non-operator). `AllowedOrigins` is also the list sign-in returns to, so ticket 04 must add `https://assistants-dashboard.herfluffness.com` to it, or a dashboard sign-in comes back to the front page.
- The header names are constants in `Accounts.Sessions.SessionCheck`; the apps (ticket 03) cannot reference the service, so they need their own.
