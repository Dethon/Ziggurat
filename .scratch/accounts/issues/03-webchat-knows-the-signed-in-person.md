# 03: WebChat knows the signed-in person

**What to build:** WebChat drops its user list, the picker and the stored name. `RegisterUser` takes no argument and reads the user from the forwarded headers; display name and avatar come from the account. Push subscriptions are keyed by user id. The E2E `WebChatStack` signs in through the accounts service with a virtual authenticator and the existing WebChat E2E suites run signed in. Spec: `../spec.md` ("WebChat").

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] A hub connection without forwarded headers is refused; with them it is registered as that user (hub tests)
- [ ] `/api/config` no longer lists users
- [ ] Push subscriptions land under the user id
- [ ] The WebChat E2E suites pass signed in
