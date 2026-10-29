# 04: Redis needs a password

**What to build:** Redis refuses a client without the deployment's Redis password, its health check authenticates, and every service that reads Redis is handed a connection string carrying it. One password, a new secret placeholder. Spec: `../spec.md` ("Redis").

**Blocked by:** 01

**Status:** done

- [x] The conformance check asserts Redis requires a password and every service depending on Redis is given a connection string
- [x] One test stack runs its Redis with a password and its suite passes
- [x] A service with a wrong password fails at startup with a message that names Redis authentication
