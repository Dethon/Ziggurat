# 03 — The probe is a test, with a real page

**What to build:** `probe/README.md`'s cases become a `[Trait("Category","Jev")]` test over the real question builders, skipped without a key: every listed pick and every `none`. Beside it, one Playwright test (the browser integration category the dismisser tests already use) serving a local page whose consent wall has a single "Entendido" control that no pattern matches: it is left standing with judgments off and dismissed with them on, and the browse result's `DismissedModals` names `judgment(0)`.

**Blocked by:** 02

**Status:** resolved

- [x] The probe test passes on `jev-1.13.0`; a `Manage preferences` beside `Got it` clicks `Got it`.
- [x] The local-page test fails with the contract absent and passes with it live.
- [x] Spec: `.scratch/jev-modal-dismissal/spec.md` § Solution.

## Answer

Shipped 2026-09-18.

- **Probe**: `Tests/Integration/Clients/ModalJudgeJevTests.cs` (`Category=Jev`, skipped without
  `typeSafe:apiKey`) over `jev-modal-cases.json` — the README's fifteen walls, run through the real
  `ModalJudge` built from the server's shipped `appsettings.json` (model, bar, cap). Every pick and
  every `none` is pinned exactly; `Manage preferences` beside `Got it` and the accept-only wall are
  named on their own. 15/15 on `jev-1.13.0` on the day it landed.
- **Local page**: `ModalJudgmentDismissalTests.Navigate_TheWallAgainstLiveJev_…` (`Category=Jev` +
  `External`) serves a route-fulfilled cookie wall with `Configurar` and `Rechazar todo` — neither in
  any word list — and asserts it is left standing with an unconfigured judge and dismissed with the
  live one, the envelope naming `judgment(1)`. **Deviation**: the ticket asked for a single
  "Entendido" control, but `entendido` is already in the cookie text patterns and the text path closes
  it; the page uses controls no pattern matches, and exercises reject-first while it is at it.
