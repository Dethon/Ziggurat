# 15 — Web workflow: search, browse, snapshot, act

**What to build:** a web request goes through search, opening the page, reading it, and only then
acting — against a site the harness serves, so the pages hold still and can be authored to
contain the exact trap the claim is about.

**Blocked by:** 05, 06.

**Status:** ready-for-agent

- [ ] The test host serves a static site, and a fake search server returns results pointing into
      it.
- [ ] A research turn searches, opens a result, reads it, and answers from what it read.
- [ ] An action turn snapshots the page before acting on it; acting first fails the scenario.
- [ ] A page whose content contradicts the search snippet is answered from the page.
- [ ] No scenario in this family reaches the public internet.
- [ ] Every scenario cites its claims and was demonstrated red once.
