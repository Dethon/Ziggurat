# 15: The migration end to end

**What to build:** Each earlier ticket brought its own household-migration step; this one proves them together. The whole household migration runs over a copy of real-shaped data (every shared store, the agents' and chat client's settings, the bots): a dry run reports everything that would move and writes nothing, a run moves it all, a rerun changes nothing; the deploy notes list the order. Spec: `../spec.md` ("The migration").

**Blocked by:** 05, 06, 09, 10, 11, 12, 13

**Status:** ready-for-agent

- [ ] A dry run over the copy reports every step's moves and writes nothing
- [ ] A run leaves the home household with its configuration and every shared store re-keyed, and a signed-in person sees their topics, schedules and spaces
- [ ] A rerun changes nothing and says so
- [ ] The deploy notes list the steps in order
