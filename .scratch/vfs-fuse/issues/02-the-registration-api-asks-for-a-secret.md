# 02 — The registration API asks for a secret

**What to build:** The agent registration API refuses `GET`, `POST` and `DELETE` without a shared secret header, following the outpost API's pattern: a placeholder in the compose secrets file, one comparison, and an unset secret refusing everything. The public proxy keeps routing to it. SexyTime, its one caller, must send the header; that change lives outside this repo and ships alongside.

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

- [x] Each verb answers unauthorized with no header, with a wrong secret, and when the secret is unset
- [x] Each verb behaves as today with the right secret
- [x] The secret is a placeholder in the compose secrets file, wired into the agent service
- [x] A comment on this ticket, when it is done, tells the person to deploy SexyTime's matching change

## Comments

Done. Every verb on `/api/agents` (`GET /api/agents`, `POST /api/agents`, `DELETE /api/agents/{agentId}`) now answers 401 unless it carries `Authorization: Bearer <secret>`, where the secret is `AGENTAPI__SHAREDSECRET` (placeholder in `DockerCompose/.env`, wired into the `agent` service). An unset secret refuses every call. The comparison is the one every shared-secret gate uses, now `Domain/Security/SharedSecret.cs` (formerly `Domain/Outposts/OutpostSecret.cs`).

**Action for the person:** deploy SexyTime's matching change alongside this one — it must send `Authorization: Bearer <AGENTAPI__SHAREDSECRET>` on every GET, POST and DELETE to `/api/agents`, and the same value must be set in the deployment's `DockerCompose/.env`. Deploying this without it makes every SexyTime registration call fail with 401.
