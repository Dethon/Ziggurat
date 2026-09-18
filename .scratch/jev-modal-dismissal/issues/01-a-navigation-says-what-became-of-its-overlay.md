# 01 — A navigation says what became of its overlay

**What to build:** The measure. `ModalDismisser` reports, per detected overlay, how it ended — `selector`, `text`, or `left-standing` — and `PlaywrightWebBrowser` publishes one `ModalDismissalEvent` per navigation that detected one, with the kind. `judgment` is reserved for ticket 02. Charted on the dashboard beside the browse metrics. Follow `.claude/rules/observability.md`; the web-search server publishes metrics the way it already publishes its browse events.

**Blocked by:** None — can start immediately.

**Status:** resolved

- [x] The existing dismisser tests pass unchanged; a detected-but-unmatched overlay yields a `left-standing` outcome.
- [x] A navigation with no overlay publishes nothing.
- [x] The dashboard shows outcomes by kind over time, verified in a browser.
- [x] Spec: `.scratch/jev-modal-dismissal/spec.md` § Telemetry.

## Answer

Shipped 2026-09-18.

- **Dismisser**: `ModalDismisser.DismissAsync` answers one `ModalOverlayOutcome` per overlay the last
  pass detected — kind, path (`Selector` / `Text` / `Judgment` / `LeftStanding`), the `ModalDismissed`
  where one closed it, and a judgment's confidence and latency. `DismissModalsAsync` keeps its
  signature as the projection to what was closed, so every existing test runs unchanged.
- **Event**: `Domain/DTOs/Metrics/ModalDismissalEvent` (`modal_dismissal`), kinds and outcomes as wire
  strings (`ModalKinds`, `ModalDismissalOutcomes`), dimensions `Kind`/`Outcome` and metrics
  `Count`/`LatencyMs` pinned by `ModalDismissalEnumsTests`. `PlaywrightWebBrowser` publishes one per
  outcome on a navigation; `ModalDismissalTelemetryTests` drives it through a real browse against
  route-fulfilled walls.
- **Host**: the web-search server had no metrics publishing at all (the ticket's "the way it already
  publishes its browse events" was not true — browse counts come from the agent's `ToolCallEvent`).
  It now registers a lazy `IConnectionMultiplexer` from `RedisConnectionString` (`redis:6379`, in its
  `appsettings.json`) and `AddMetricsPublishing("mcp-websearch")`, so it also appears on the health
  roster; the compose service depends on `redis`. `MetricsRegistrationContractTests` gained its row.
  The connection carries metrics alone — page images still cross at the agent's bridge.
- **Dashboard**: there was no web page and no browse metrics to sit beside, so this is a new `Web`
  family at `/web` (globe in the sidebar): outcome series over time, a breakdown by kind or outcome
  (count, judgment latency), and the recent overlays. Collector keys `metrics:modals:<day>`, totals
  `modals:<outcome>:count`, push `OnModalDismissal`; endpoints `/api/metrics/modals`, `/modals/by/{dimension}`,
  `/modals/trend?kind=`.

**Review follow-up (same day).** The trend is drawn per kind: a Kind pill (all / cookie / age /
newsletter / notification) on the Web page passes `kind=` to `/modals/trend`, riding the breakdown
refresh so the pill moves it and a push brings it back into line.
