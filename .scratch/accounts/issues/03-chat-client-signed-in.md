# 03: The chat client, signed in

**What to build:** A person signs in once and the chat client knows them: the proxy checks the session in front of the chat client, its hub and its attachment and dictation endpoints, copying the verified headers over anything the client sent; the hub registers the connection as that person; the user list and picker go; name and avatar come from the account; push subscriptions follow the user id. The E2E stack signs in with a virtual authenticator. Spec: `../spec.md` ("The session check at the proxy", "The chat client and its hub").

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] A signed-out browser lands on sign-in and comes back; a signed-in one reaches the chat client as themselves (E2E)
- [ ] A hub connection without the verified headers is refused
- [ ] The configuration endpoint no longer lists users
- [ ] Push subscriptions are stored under the user id
- [ ] The conformance check asserts the protected routes go through the session check and copy its headers, and the login handoff's routes don't
- [ ] The existing chat-client E2E suites pass signed in
