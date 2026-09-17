# 03 — The probe is a test, with a real page

**What to build:** `probe/README.md`'s cases become a `[Trait("Category","Jev")]` test over the real question builders, skipped without a key: every listed pick and every `none`. Beside it, one Playwright test (the browser integration category the dismisser tests already use) serving a local page whose consent wall has a single "Entendido" control that no pattern matches: it is left standing with judgments off and dismissed with them on, and the browse result's `DismissedModals` names `judgment(0)`.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] The probe test passes on `jev-1.13.0`; a `Manage preferences` beside `Got it` clicks `Got it`.
- [ ] The local-page test fails with the contract absent and passes with it live.
- [ ] Spec: `.scratch/jev-modal-dismissal/spec.md` § Solution.
