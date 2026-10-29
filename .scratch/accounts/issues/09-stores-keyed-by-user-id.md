# 09: Memory, logins and schedules follow the user id

**What to build:** Memory keys, the profile, recall and extraction take the principal's user id, never `Sender`; a turn with no user recalls and extracts nothing. Web login records are sealed with the user id as associated data. Schedules stamp `CreatedBy` with the principal's user. Custom agents are keyed by user id (still in memory). Rules (`memory.md`, `scheduling.md`) follow. Spec: `../spec.md` ("Keyed by user id").

**Blocked by:** 06

**Status:** ready-for-agent

- [ ] A fact told on Telegram is recalled on WebChat by the same person and not by another (memory tests)
- [ ] A login saved by a person is readable under their user id only
- [ ] A schedule written by a person records their user id
