# 12: Skill, rules and eval scenarios

**What to build:** The model knows how to browse as a person. The `web-browsing` skill teaches `asMe` (only when the task needs the person's account), the handoff (call `web_login`, give the link, stop; the wake resumes), "a logged-out page under `asMe` means hand off", a worker reporting a login wall to its parent, and a fire reporting one instead of minting a link. Each claim is declared beside its prose and gets eval scenarios. The web browsing rules file gains sections on contexts, the handoff and the password rule. Spec: `../spec.md` (Testing Decisions).

**Blocked by:** 07, 11

**Status:** done

- [x] Skill prose and claims for each behaviour above, with scenarios on hermetic web fixtures
- [x] Changed-tier eval green for the new claims
- [x] The web browsing rules file documents person contexts, the handoff tab, the wake and the password rule

## Comments

2026-10-25 — changed tier, jonas on deepseek-v4.1-flash-1017: the new claims' scenarios are green (orders handed off 2/2, morning check reports the wall 2/3, museum browsed without asMe 2/2). Four ref-chaining form scenarios that cite none of the new claims are red (0–25%): the model drops `ref` from `web_action` after the first call, on Wafer and OpenInference alike. Master (633267034) reds three of them at the same rates with the same drop, so it predates this work. The worker half of the login-wall claim is exempt (held by the tool set); no scenario runs a real worker into a login wall yet.
