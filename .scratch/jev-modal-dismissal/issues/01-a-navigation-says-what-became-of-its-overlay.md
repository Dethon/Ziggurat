# 01 — A navigation says what became of its overlay

**What to build:** The measure. `ModalDismisser` reports, per detected overlay, how it ended — `selector`, `text`, or `left-standing` — and `PlaywrightWebBrowser` publishes one `ModalDismissalEvent` per navigation that detected one, with the kind. `judgment` is reserved for ticket 02. Charted on the dashboard beside the browse metrics. Follow `.claude/rules/observability.md`; the web-search server publishes metrics the way it already publishes its browse events.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The existing dismisser tests pass unchanged; a detected-but-unmatched overlay yields a `left-standing` outcome.
- [ ] A navigation with no overlay publishes nothing.
- [ ] The dashboard shows outcomes by kind over time, verified in a browser.
- [ ] Spec: `.scratch/jev-modal-dismissal/spec.md` § Telemetry.
