# 09: Browse links open the household's own web search

**What to build:** A browse link a household's agent sends names the household whose web search
serves it. The relay forwards the page through that household's tunnel instead of the home's. When
that household's web search is not connected, the person gets the offline page. Spec: `../spec.md`.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] A non-home household's browse link opens a page served by that household's web search (round trip, browse pages)
- [x] With that household's web search not connected, the link shows the offline page and never the home's web search
- [x] The home's browse links are unchanged
