# 06 — Fetch page images by ref

**What to build:** The model can finally look at a picture on a page. It names the handles it wants from what browsing listed, and the pictures come back through the same session that found them — so an image served only to a browser carrying the right cookies still arrives. Asking for several at once costs one call, not one call each. When a picture cannot be shown, the model is told which wall it hit, so it can tell a failure worth retrying from one that never will be.

This is the ticket that makes the feature real.

**Blocked by:** 05 — Lift image bytes at the MCP bridge.

- [ ] A call naming one or more image refs returns those pictures, and the model sees them
- [ ] Pictures are fetched through the live browser session, carrying its cookies, referer and fingerprint
- [ ] A call naming more than eight images returns the first eight and names the rest, rather than failing whole
- [ ] A ref from a session that has since closed is refused with a message saying to browse the page again
- [ ] A ref that was never an image — an element ref, say — is refused by name rather than failing to be found
- [ ] A model that cannot accept images is told so, and no bytes are fetched or stored
- [ ] A site refusing the fetch produces its own distinct message
- [ ] An image whose bytes have already been forgotten produces its own distinct message, naming what it was
- [ ] All six refusals are distinct — a model can tell a retryable failure from a permanent one
- [ ] Covered by unit tests for the cap, the ref namespace check and each refusal message, plus an integration test fetching a real image through a live session
