# 02 — The Domain asks typed questions and one client answers

**What to build:** The typed-judgment contract in Domain — a state, named choice and noul questions, typed answers with probabilities and confidence, usage — and the one Infrastructure client that answers it from TypeSafe's `POST /v1/systemone`. A pooled warm connection, the caller's deadline honoured as a cancellation, 429/529/timeouts/transport errors answered as absence (never thrown into a caller), 401/422 logged loudly once as configuration errors. Settings `typeSafe: { apiUrl, apiKey, model }` in `Agent/appsettings.json` with `model` pinned to `jev-1.13.0`; the key a secret — placeholder in `DockerCompose/.env`, `${TYPESAFE_API_KEY}` on the agent in compose. An empty key registers a client that answers absence, so nothing downstream checks for the key itself. Test-first against a fake handler: the request body's exact shape, each answer type parsed, each failure mapped.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] A choice and two nouls asked together produce one request whose JSON matches the documented shape (`state`, `model`, `questions` keyed by id, `criteria` as a map for a choice).
- [ ] Choice answers expose the pick, its confidence and the per-option probabilities; noul answers the probability; the response's model and input tokens are exposed.
- [ ] 429, 529, a timeout, a cancelled deadline and a connection failure each answer absence and throw nothing; 401 and 422 answer absence and log an error naming the setting to fix.
- [ ] An empty `apiKey` makes no HTTP call at all.
- [ ] Domain references nothing of HTTP or TypeSafe by name; the contract carries no score type until a use needs one.
- [ ] `DockerCompose/.env` holds a placeholder, compose wires `${TYPESAFE_API_KEY}`, and no real key is in any tracked file.
- [ ] Spec: `.scratch/jev-skill-preload/spec.md` § The client and the contract.
