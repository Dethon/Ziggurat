# 03: Redis needs a password

**What to build:** Redis starts with `--requirepass ${REDIS_PASSWORD}` (placeholder in `.env`), its healthcheck authenticates, and every service that reads Redis is handed a connection string carrying the password in its own environment. One password for the deployment. Spec: `../spec.md` ("Redis").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] The compose conformance test asserts Redis requires a password and every service depending on Redis gets a connection string variable
- [ ] One test stack (E2E `WebChatStack` or the integration fixture) runs its Redis with a password and its suite passes
- [ ] A service whose connection string has the wrong password fails at startup with a message that names Redis auth
