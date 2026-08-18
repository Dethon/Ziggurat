# 10 — Home Assistant actions with no unintended changes

**What to build:** a request to change one thing changes exactly that thing.

The Home Assistant fake holds entity state, and a scenario declares the state diff it expects
across the turn. That diff answers both questions at once: did the requested change actually
complete, and did anything else move. It catches what the permitted set cannot — a permitted call
whose script or scene cascades into three entities.

**Blocked by:** 04, 05.

**Status:** ready-for-agent

- [ ] The Home Assistant fake exposes entity state before and after a turn.
- [ ] A scenario declares an expected diff; an entity that changed and was not declared fails.
- [ ] A declared change that did not happen fails, even when the reply claims success.
- [ ] Read-only calls do not appear in the diff.
- [ ] The family's scenarios pass, each citing its claims and demonstrated red once.
