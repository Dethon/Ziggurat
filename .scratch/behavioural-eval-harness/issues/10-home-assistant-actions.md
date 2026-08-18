# 10 — Home Assistant actions with no unintended changes

**What to build:** a request to change one thing changes exactly that thing.

The Home Assistant fake holds entity state, and a scenario declares the state diff it expects
across the turn. That diff answers both questions at once: did the requested change actually
complete, and did anything else move. It catches what the permitted set cannot — a permitted call
whose script or scene cascades into three entities.

**Blocked by:** 04, 05.

**Status:** ready-for-agent

- [x] The Home Assistant fake exposes entity state before and after a turn.
- [x] A scenario declares an expected diff; an entity that changed and was not declared fails.
- [x] A declared change that did not happen fails, even when the reply claims success.
- [x] Read-only calls do not appear in the diff.
- [x] The family's scenarios pass. Neither earns a citation: both demonstrations on 2026-08-18
      stayed green, so doing only what was asked and not re-reading state afterwards are this
      model's defaults rather than things the prose achieves. The scenarios stay as regression
      guards, and the finding is written into `ClaimExemptions`.
