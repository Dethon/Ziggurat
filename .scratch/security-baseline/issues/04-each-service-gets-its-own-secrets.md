# 04: Each service gets only its own secrets

**What to build:** No `env_file` in the compose file. Each service's `environment` names, as `${VAR}`, only the variables it reads, so the WebChat host and the dashboard no longer hold the OpenRouter key, the HA token or the logins key. `.env` stays tracked as placeholders; `Announce__Token` loses its working default. The Linux and Windows overrides mount `usersecrets/<UserSecretsId>` per project instead of the whole directory (Agent and Observability share one id). Spec: `../spec.md` ("Per-service environment", "No working defaults").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A compose conformance test fails on any `env_file`, and on the OpenRouter key, HA token, logins key or Telegram tokens reaching a service that doesn't read them
- [ ] Every variable in `.env` is referenced by at least one service, asserted by the test
- [ ] `Announce__Token` is empty in `.env`
- [ ] Each override mounts only the project's own user-secrets directory, asserted by the test
- [ ] The stack comes up with the `launch-stack` command and WebChat answers a turn (manual check, noted in the ticket)
