# 03: The chat client, signed in

**What to build:** A person signs in once and the chat client knows them: the proxy checks the session in front of the chat client, its hub and its attachment and dictation endpoints, copying the verified headers over anything the client sent; the hub registers the connection as that person; the user list and picker go; name and avatar come from the account; push subscriptions follow the user id. The E2E stack signs in with a virtual authenticator. Spec: `../spec.md` ("The session check at the proxy", "The chat client and its hub").

**Blocked by:** 02

**Status:** done

- [x] A signed-out browser lands on sign-in and comes back; a signed-in one reaches the chat client as themselves (E2E)
- [x] A hub connection without the verified headers is refused
- [x] The configuration endpoint no longer lists users
- [x] Push subscriptions are stored under the user id
- [x] The conformance check asserts the protected routes go through the session check and copy its headers, and the login handoff's routes don't
- [x] The existing chat-client E2E suites pass signed in

Notes for later tickets (`.claude/rules/channels.md`, `accounts.md` and `testing.md` have the detail):

- The hub's person is `ForwardedAccount` (`Domain/Security`: user, tenant, roles from the three headers), kept in `Context.Items[ChatHub.AccountItem]` by `RegisteredConnectionFilter.OnConnectedAsync`; a connection without all three is closed as not signed in. `RegisterUser` is gone, and the client no longer waits for a registration (`ConnectionRegistered`/`RegisteredAgain` removed; catch-up keys on `BecameLiveAgain` again).
- Interim, until 06: a chat-client turn's `Sender`, a user message's `SenderId` and push subscriptions are the account's opaque user id, so memory and principals for WebChat turns key off that id, and another person's message in a shared topic shows the id rather than a name (the client names only its own person, from `GET /accounts/me`). WebChat stays off `Identity:VerifiedChannels`.
- `create-user --name <n> [--role owner|member]` is the operator's (and the E2E stack's) way to add a person; `GET /accounts/me` answers who is signed in.
- The E2E stack browses Caddy at `localhost` on a port it takes up front and seeds each `NextUserIndex()` as an account on first asking; `fixture.CreateSignedInPageAsync(index)` is how a test opens a page.
- `/api/agents*` is not behind the check (ticket 15), nor is SexyTime's catch-all.
- A host run of WebUI and the SignalR channel (`Local: ...`) has no proxy to forward the person, so the hub refuses every connection there.
