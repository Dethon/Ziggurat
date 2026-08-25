# 03 — Measure images on the live page

**What to build:** The size filter starts working on real pages. The browser measures how large each image actually renders and stamps that onto the page before the text is extracted, so decoration is filtered by what the reader would have seen rather than by markup that frequently lies or says nothing at all.

After this ticket, browsing a real site lists that site's real pictures and omits its furniture.

**Blocked by:** 02 — Never split an image entry, and count the rest.

- [x] Surviving images carry their rendered dimensions by the time the page text is extracted
- [x] Extraction stays a pure function of a page's markup — the measuring step happens before it, not inside it
- [x] An image sized only by stylesheet, with no markup dimensions, is filtered on its rendered size
- [x] An image whose markup dimensions disagree with its rendered size is filtered on the rendered size
- [x] Measuring adds no round trip per image
- [x] Covered by an integration test against the real browser container, at the existing browser integration seam
