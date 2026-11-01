# 16: The smoke tier checks household config

**What to build:** A wiring mistake is caught before a deploy: on a real stack after the migration, a signed-in reply comes from the home household's agent paid by its key, and the stores answer by household. Spec: `../spec.md` (Testing Decisions, seam 7).

**Blocked by:** 07, 15

**Status:** done

- [x] A signed-in reply comes from the home household's agent with its configured name
- [x] A topic and a schedule are listed under the home household
- [x] The smoke suite passes on a fresh `scripts/smoke-stack.sh up`

`scripts/smoke-stack.sh up` runs the real household migration (deploy steps 5–8: operator first, one
person, no keys, agent restart, `redelegate --yes`) instead of seeding two sections by hand. The topic
is listed by the hub (the chat client's list after a reload) and kept under `topic@{home}:…`; no API
outside the agent lists schedules, so the schedule is checked in its household-keyed keys and by
`redelegate` having read it through the scheduling server and saved its delegation.
