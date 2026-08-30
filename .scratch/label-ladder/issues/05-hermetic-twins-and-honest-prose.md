# 05 — Hermetic twins and honest prose

**What to build:** Every acquisition rung has deterministic coverage a bare test run can trust, and the prose stops pledging what the code now holds by construction. The route-fulfilment fake-CDN pattern gains twins for the rungs it does not yet cover — the same-origin credentialed wire fetch, and the vector-image-leaves-as-pixels rule — so third parties stop being the only witness. The three live-site tests remain in the external category as attestation only, and the one that hard-asserts navigation gains the same skip-on-unreachable courtesy as its siblings. The web-browsing rules file's "written twice and must move as one" paragraph is rewritten to what code cannot hold: the one-label invariant by name, the cap-is-a-safeguard rationale, the credentials trap.

**Blocked by:** 01, 02, 03, 04 — this ticket seals the migration.

**Status:** ready-for-agent

- [x] A hermetic twin proves the same-origin credentialed wire fetch rung against a route-fulfilled page, no third party involved.
- [x] A hermetic twin proves an as-served vector image is answered as pixels, not as the file, no third party involved.
- [x] All three live-site tests carry the external category and skip — never fail — on any third-party precondition (unreachable site, changed gallery, missing picture).
- [x] The rules file no longer claims the ladder exists twice; its rewritten section states the one-label invariant, the cap rationale, and the credentials trap, and points at the module for the rest.
- [x] A bare test run is green with the network's third parties unreachable.

## Comments

- 2026-08-30: Implemented on feat/label-ladder. All checklist items verified by the unit and hermetic integration suites.
