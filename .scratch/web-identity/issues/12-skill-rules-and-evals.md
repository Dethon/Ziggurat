# 12: Skill, rules and eval scenarios

**What to build:** The model knows how to browse as a person. The `web-browsing` skill teaches `asMe` (only when the task needs the person's account), the handoff (call `web_login`, give the link, stop; the wake resumes), "a logged-out page under `asMe` means hand off", a worker reporting a login wall to its parent, and a fire reporting one instead of minting a link. Each claim is declared beside its prose and gets eval scenarios. The web browsing rules file gains sections on contexts, the handoff and the password rule. Spec: `../spec.md` (Testing Decisions).

**Blocked by:** 07, 11

**Status:** ready-for-agent

- [ ] Skill prose and claims for each behaviour above, with scenarios on hermetic web fixtures
- [ ] Changed-tier eval green for the new claims
- [ ] The web browsing rules file documents person contexts, the handoff tab, the wake and the password rule
