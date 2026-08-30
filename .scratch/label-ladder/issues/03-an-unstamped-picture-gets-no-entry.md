# 03 — An unstamped picture gets no entry

**What to build:** A picture the page never measured gets no entry and no ref — the converter stops inventing numbers. The converter's fallback counter (a numbering path only hand-written test markup ever took) is deleted; an image without a stamped ref is treated like a non-survivor, because an unrouteable ref is a handle the model would act on and be refused by. Unit fixtures stamp refs and rendered dimensions the way the page does, so the suite exercises the numbering production actually runs.

**Blocked by:** None — can start immediately (converter-side seam, parallel to 01/02).

**Status:** ready-for-agent

- [ ] The fallback numbering path no longer exists; the stamped ref is the only source of an entry's ref.
- [ ] An image with no stamped ref yields no entry and no ref, landed red-first — in body position, inside a table, and inside a link alike.
- [ ] Every converter unit fixture that expects an entry stamps a ref and dimensions the way the page does.
- [ ] The nested-table regression (one picture never listed twice) still holds on the single numbering path.
- [ ] Existing entry-position and entry-shape facts pass unchanged in what they assert.

## Comments
