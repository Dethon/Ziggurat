# 02 — The registration API asks for a secret

**What to build:** The agent registration API refuses `GET`, `POST` and `DELETE` without a shared secret header, following the outpost API's pattern: a placeholder in the compose secrets file, one comparison, and an unset secret refusing everything. The public proxy keeps routing to it. SexyTime, its one caller, must send the header; that change lives outside this repo and ships alongside.

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

- [ ] Each verb answers unauthorized with no header, with a wrong secret, and when the secret is unset
- [ ] Each verb behaves as today with the right secret
- [ ] The secret is a placeholder in the compose secrets file, wired into the agent service
- [ ] A comment on this ticket, when it is done, tells the person to deploy SexyTime's matching change
