# 03 — A ref finds its tab

Status: resolved
Blocked by: 01, 02

**What to build:** `web_action` and `view_image` route each ref to the tab that stamped it,
whichever tab that is — the model names what it wants and the want finds its page. The two
ref-less operations, `web_snapshot` and `back`, address the current tab (the tab last touched, per
ticket 02), and `back` walks that tab's own history. Fetching an image from an old tab is a touch:
it refocuses that tab for the next ref-less call.

- [x] `view_image` with refs from two different tabs answers each picture from the page that listed it, in one call
- [x] `web_action` acts on the tab its ref names, even when another tab is current
- [x] `web_snapshot` captures the current tab; after a `view_image` on an older tab, that older tab is what it captures
- [x] `back` navigates the current tab's own history, not another tab's
- [x] Acting on a routed ref makes its tab current
- [x] Covered at the tool unit seam (routing decisions over a faked browser) and the integration seam (a real action landing on the tab its ref names, a cross-tab image fetch)
