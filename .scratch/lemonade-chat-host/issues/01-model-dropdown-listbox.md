# 01 — The model dropdown becomes a custom listbox

**What to build:** In WebChat's gear menu, the model picker stops being a native select and becomes a custom listbox built on the same pattern the agent selector beside it already uses: a trigger, an option list with listbox and option roles, keyboard navigation, selection state, and the same "picked model differs from the default" behaviour that produces a config patch. A person using it sees exactly what they saw before: the same models, the same names, the same override summary on the gear button. This is a prefactor so a later ticket can put an icon beside an entry, which a native option cannot hold.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] The gear menu's model picker renders every patchable model the catalog offers, in the same order as before, and picking one produces the same config patch as before
- [x] The picker is keyboard operable and carries listbox and option roles, matching the agent selector's pattern
- [x] The reasoning effort picker and the gear summary behave exactly as before
- [x] The drawn-icon conformance test and every existing WebChat unit and E2E test stay green
- [x] No new icon, no Lemonade behaviour: this ticket changes structure only
