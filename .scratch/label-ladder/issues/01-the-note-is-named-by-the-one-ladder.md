# 01 — The note is named by the one ladder

**What to build:** A fetched picture's note uses exactly the words of the image entry the model chose it by, because both are named by the same ladder in the same language. The fetch script stops computing a label: it answers only facts — the strings the page offers about the picture (description, associated caption, advisory title, enclosing link's words, source address) and the stamped rendered dimensions — and the one C# ladder names the note from them. The ladder itself becomes a pure function over a facts record, with one record builder on the extraction side (from the parsed page) and one on the fetch side (from the probe's answer). The in-page ladder is deleted, not generated.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The ladder is a pure facts-in/label-out function; the extraction path and the fetch path both call it and nothing else computes a label.
- [ ] The fetch script contains no label logic — no rung order, no sanitising, no cap; it returns the raw strings and stamped dimensions.
- [ ] Rung order, data-URI exclusion, blank-not-falsy fallback, bracket rounding, whitespace flattening, and the cap are each stated exactly once.
- [ ] The formerly-divergent Unicode edges (the whitespace set on U+0085 and U+FEFF; the filename gate's end-anchoring against a trailing newline) are named unit facts on the surviving ladder, landed red-first, with C# semantics the surviving behaviour.
- [ ] The existing entry-shape unit tests pass re-aimed at the unified function; the integration fact "a long label is cut the way the entry cut it" still holds.
- [ ] No caller above the browser observes any change beyond label wording on the divergent edges.

## Comments
