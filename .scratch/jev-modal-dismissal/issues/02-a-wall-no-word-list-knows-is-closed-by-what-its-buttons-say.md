# 02 — A wall no word list knows is closed by what its buttons say

**What to build:** The judgment. After the selector and text paths return nothing for a detected overlay, the overlay's buttons and links go to the typed-judgment contract as `{ overlay_kind, controls }` with the kind's questions (spec § Questions), and the pick — a confident reject first on a cookie wall — is clicked by index into the same locator list. `McpServerWebSearch` registers the client and its settings, with the key wired on its compose service and a placeholder in `.env`. A stale click, a `none`, a pick under the bar, or no answer leaves the page as today with outcome `left-standing`. Test-first against a fake contract at the dismisser level.

**Blocked by:** 01, and `.scratch/jev-skill-preload/issues/02`

**Status:** resolved

- [x] With a fake contract, a cookie overlay whose controls are `["Configurar","Aceptar todo","Rechazar todo"]` clicks index 2; with `reject` answering `none` and `accept` answering 1, index 1; with both under the bar, nothing.
- [x] `["Iniciar sesión","Buscar","Menú"]` with both answers `none` clicks nothing.
- [x] A newsletter overlay is asked one question; a cookie overlay two, in one request.
- [x] More than `maxControls` controls sends the first `maxControls` in document order.
- [x] No page url or text is in the state — asserted on the request the fake received.
- [x] A call slower than the deadline leaves the page and reports `left-standing`; the navigation's total added time is bounded by the deadline.
- [x] `McpServerTableTests` / the server contract tests still pass with the new registration; an empty key makes no call.
- [x] Spec: `.scratch/jev-modal-dismissal/spec.md` § Decisions.

## Answer

Shipped 2026-09-18.

- **Domain** `Domain/Tools/Web/ModalJudge.cs` (+ `ModalJudgmentSettings`, `ModalControl`, `ModalPick`):
  `Ask` builds `{ overlay_kind, controls[{index, role, name}] }` with the kind's questions — cookie
  `reject` then `accept`, age `enter`, newsletter `decline`, notification `deny`; criteria one per
  index (`button "Rechazar todo"`) plus `none`; first `maxControls` in document order; `Generic` is
  not asked. `Decide` takes the first confident pick of a listed control in question order; `none`
  and anything under the bar or naming no sent control are `None`; a missing or non-choice answer,
  an absence, or an answer that lands after the deadline are `Absent`. `PickAsync` runs under
  `judgment.deadlineMs` through `TimeProvider`. `ModalJudgeTests` covers the ticket's cases against
  `StubJudge`, the deadline with a `FakeTimeProvider`.
- **Dismisser**: after the window, overlays both cheap paths left standing go to the judge together.
  The controls are listed in one round trip from the locator chained under the container selector
  (`button, a, input[type=submit|button], [role=button]`) — visible, inside a container the shared
  overlay predicate accepts, not an anchor off the page, named by the same accessible-name script the
  text path narrows on — and the pick is clicked as `Nth(index)` into that locator. A stale click, a
  navigation or a non-pick is `left-standing` carrying the judge's confidence and latency. The overlay
  predicate, the name script and the anchor guard became one JS constant each.
- **Host**: `McpSettings.TypeSafe` / `Judgment`; `appsettings.json` ships `jev-1.13.0`, empty key,
  `1000 / 0.6 / 20`. `ConfigModule.AddTypeSafe` registers `IJudge` via `TypeSafeJudge.Create` on the
  shared pool plus the keep-alive when a key is present; the browser gets
  `new ModalDismisser(new ModalJudge(...))`. The key arrives as `TYPESAFE__APIKEY` through the
  service's existing `env_file: .env` — the same line the agent reads, not a second variable.
- **Tests**: `ModalJudgmentDismissalTests` (browser, scripted judge) — pick by index with the envelope
  naming `judgment(1)`, a `none` leaving the wall and counting the miss, the judge shown the wall's
  controls and nothing of the page, an unconfigured judge leaving the page as today;
  `ModalJudgmentRegistrationTests` — empty key answers `Unconfigured` with no keep-alive, shipped
  settings bind.
