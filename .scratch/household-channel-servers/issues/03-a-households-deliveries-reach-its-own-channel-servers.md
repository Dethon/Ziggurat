# 03: A household's deliveries reach its own channel servers

**What to build:** What a household's turn sends out on a channel by its id reaches that household's
own connection, or the deployment's, and never another household's. That covers:
- a schedule's, a watch's or a login wake's delivery;
- the review copy's host;
- the topic created ahead of a turn for an attach-only target;
- an attachment fetched for hydration, from the connection that took it.

The agent's internal topic creation refuses a person whose household is not the household of the
tunnel the request came through. Spec: `../spec.md`.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] A household's scheduled reminder delivering to voice reaches its own voice server and not another household's (round trip)
- [ ] A topic created ahead for an attach-only target is created on the household's own connection
- [ ] An attachment is fetched from the connection of the household that took it
- [ ] Topic creation through a household's tunnel refuses another household's person
- [ ] The home's deliveries are unchanged
