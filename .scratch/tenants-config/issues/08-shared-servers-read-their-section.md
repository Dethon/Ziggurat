# 08: Shared servers read their household's section

**What to build:** A shared server reads its section of the calling household's configuration through one accessor over the verified call, cached and invalidated: Idealista its keys, scheduling and Sugary the household's time zone. Spec: `../spec.md` ("Shared servers").

**Blocked by:** 05

**Status:** done

- [x] Idealista searches with the calling household's keys
- [x] A schedule is read and fired in its household's time zone
- [x] A saved section reaches the server without a restart
- [x] No tool reads configuration around the accessor (conformance)

Notes: the accessor is `call.Household` / `CallHousehold` (`Mcp.Hosting/CallHousehold.cs`,
`.claude/rules/household-config.md`, "A shared server's accessor"). A cron's fire proves its
household with the schedule's stored delegation, a proof the accounts service takes from the
scheduling server alone. Idealista and Sugary keep their answers for the lifetime alone (no Redis,
deliberately); scheduling hears saves on its own Redis. The deployment's Idealista key is gone: on
deploy the home's keys go into its `keys` section with `set-household-config`. Sugary's pen reminders
stay on the server's zone (no proof without a call). Left: the scheduling skill and both mounts'
descriptions still name the server's zone (served per session); the agent still reads a bare
`runAt` in its own zone for the delegation's digest (06).
