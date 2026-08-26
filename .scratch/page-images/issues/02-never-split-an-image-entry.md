# 02 — Never split an image entry, and count the rest

Status: resolved

**What to build:** A page long enough to be cut off never ends mid-entry, so every image handle the model can read is a handle it can actually use. When pictures lie beyond the part of the page returned, the model is told how many, so it knows whether paging forward would show it more.

Blocked by: 01

- [x] A body cut short backs up past a partial image entry the way it already backs up to a line boundary
- [x] An entry straddling the cut point is dropped whole rather than truncated
- [x] An entry ending exactly at the cut point is kept intact
- [x] The result reports how many images lie beyond the returned window
- [x] Existing paging arithmetic is unchanged — the reported total content length remains the pre-slice total
- [x] Covered by unit tests at the existing truncation seam, which is a pure string function with a single caller
