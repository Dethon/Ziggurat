# 08: Shared servers read their household's section

**What to build:** A shared server reads its section of the calling household's configuration through one accessor over the verified call, cached and invalidated: Idealista its keys, scheduling and Sugary the household's time zone. Spec: `../spec.md` ("Shared servers").

**Blocked by:** 05

**Status:** ready-for-agent

- [ ] Idealista searches with the calling household's keys
- [ ] A schedule is read and fired in its household's time zone
- [ ] A saved section reaches the server without a restart
- [ ] No tool reads configuration around the accessor (conformance)
