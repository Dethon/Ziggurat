# 01 — List images in browsed page text

**What to build:** Browsing a page returns its pictures listed in the page text, each where the picture actually sits, so the model reading the text can tell which image belongs to which section. Every listed image carries a handle and the best name the page gives it. Page furniture — spacers, icons, tracking pixels — is left out entirely and gets no handle.

This ticket delivers the catalogue only. Handles do not resolve to anything yet; nothing fetches an image. What is verifiable is that a page's text now names its pictures, in order, with useful labels.

**Blocked by:** None — can start immediately.

- [ ] An image that survives filtering appears in the page text at the position it occupies in the document, not gathered into a list elsewhere
- [ ] Each entry carries an image ref, spelled in its own namespace and never mixed with the element refs the accessibility snapshot assigns
- [ ] Labels resolve by falling back: alt text, then figcaption, then title, then enclosing link text, then filename
- [ ] An image that survives all five fallbacks is listed with its rendered dimensions rather than a blank or generic label
- [ ] An image under roughly 100px on either side gets no entry and no ref, and cannot be named by any later call
- [ ] The size decision reads dimensions annotated onto the element, not markup width/height attributes
- [ ] Image address information is preserved for images that pass the filter and still stripped for those that do not
- [ ] Covered by unit tests at the existing page-text extraction seam, using hand-written HTML and no browser
