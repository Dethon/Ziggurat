# 02 — The descent is decided in C#

**What to build:** The byte-acquisition descent — wire fetch, canvas read, context request, element screenshot, each step down trading fidelity for reach — is owned by one internal module that decides every step in C#. Each in-page rung is a separate probe call behind one narrow per-rung probe interface whose only production implementation is Playwright-backed; the whole sequence runs inside the locked tab work callback, so the page cannot change between calls. The stringly return protocol is replaced by typed per-rung answers, mapped by the module onto the existing fetch statuses, which do not change. The wire-raster acceptance rule is stated once, compared once, media type trimmed once.

**Blocked by:** 01 — The note is named by the one ladder.

**Status:** ready-for-agent

- [x] The descent module's ordering is asserted against a faked probe: wire fails → canvas; canvas taints → context request; context request refused → element screenshot; never-loaded answers its own wall, not the site-refused one.
- [x] The credentials rule is preserved verbatim and unit-asserted: same-origin with the page's credentials, cross-origin anonymously — never credentialed cross-origin.
- [x] The wire-raster set exists as one constant with one comparison; a media type with stray whitespace is judged the same on every path.
- [x] The browser retains page choreography only — no acquisition ordering, no per-rung decisions remain in it or in the page script.
- [x] Every existing fetch status and wall is produced by the module exactly as before; callers observe no contract change.
- [x] The existing hermetic fake-CDN integration facts (shared-CDN wire fetch; CORS-withheld falling to screenshot) still pass unmodified in what they assert.

## Comments

- 2026-08-30: Implemented on feat/label-ladder. All checklist items verified by the unit and hermetic integration suites.
