# 02 — A wall no word list knows is closed by what its buttons say

**What to build:** The judgment. After the selector and text paths return nothing for a detected overlay, the overlay's buttons and links go to the typed-judgment contract as `{ overlay_kind, controls }` with the kind's questions (spec § Questions), and the pick — a confident reject first on a cookie wall — is clicked by index into the same locator list. `McpServerWebSearch` registers the client and its settings, with the key wired on its compose service and a placeholder in `.env`. A stale click, a `none`, a pick under the bar, or no answer leaves the page as today with outcome `left-standing`. Test-first against a fake contract at the dismisser level.

**Blocked by:** 01, and `.scratch/jev-skill-preload/issues/02`

**Status:** ready-for-agent

- [ ] With a fake contract, a cookie overlay whose controls are `["Configurar","Aceptar todo","Rechazar todo"]` clicks index 2; with `reject` answering `none` and `accept` answering 1, index 1; with both under the bar, nothing.
- [ ] `["Iniciar sesión","Buscar","Menú"]` with both answers `none` clicks nothing.
- [ ] A newsletter overlay is asked one question; a cookie overlay two, in one request.
- [ ] More than `maxControls` controls sends the first `maxControls` in document order.
- [ ] No page url or text is in the state — asserted on the request the fake received.
- [ ] A call slower than the deadline leaves the page and reports `left-standing`; the navigation's total added time is bounded by the deadline.
- [ ] `McpServerTableTests` / the server contract tests still pass with the new registration; an empty key makes no call.
- [ ] Spec: `.scratch/jev-modal-dismissal/spec.md` § Decisions.
