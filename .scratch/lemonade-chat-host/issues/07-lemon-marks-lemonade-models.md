# 07 — The lemon marks Lemonade models

**What to build:** In WebChat's gear menu, every Lemonade model shows a lemon before its name in the model listbox, and when a Lemonade override is active the gear button's summary shows the same lemon before the model name. The lemon is a new drawn glyph in the client's icon table, rendered only through the icon component; "is a Lemonade model" is derived from the id's prefix, so the catalog DTO gains no field. Non-Lemonade models look exactly as they do today.

**Blocked by:** 01 — The model dropdown becomes a custom listbox; 04 — Lemonade models appear in the selector.

**Status:** done

- [x] Each listbox entry whose id carries the Lemonade prefix renders the lemon glyph before its display name; other entries render no glyph
- [x] The gear summary renders the lemon before the model name when the active override is a Lemonade model, and not otherwise
- [x] The lemon exists only as a glyph in the icon table and is drawn through the icon component; the drawn-icon conformance test stays green
- [x] Selector or store tests pin the marker as a pure function of the id, following the agent settings selector tests
- [x] The listbox on a phone-width layout and in dark theme keeps the glyph legible beside the name
