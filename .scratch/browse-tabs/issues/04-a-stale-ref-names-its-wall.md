# 04 — A stale ref names its wall

Status: resolved
Blocked by: 03

**What to build:** A ref that no longer resolves refuses through one of two walls, each naming its
recovery. *Superseded* — the tab is open but a later snapshot or an in-tab navigation renumbered
its refs — says to snapshot or browse the current page again. *Closed* — the tab was evicted or
the session expired — names the URL the ref belonged to and says to browse it again. The session
keeps a small bounded registry of ref range → URL, with tombstones surviving eviction, so the
closed wall can name its page. The registry dies with the session.

- [x] A ref renumbered by a later snapshot of a still-open tab refuses as superseded, pointing at the current page
- [x] A ref from a tab evicted at the cap refuses as closed, naming the URL it belonged to
- [x] A ref from an expired session keeps the existing dead-session refusal — session death is not a third wall
- [x] Element refs and image refs refuse through the same two walls with the same recovery story
- [x] The two new sentences read differently from each other and from every existing refusal
- [x] The registry is bounded and holds nothing after its session closes
- [x] Covered at the session-management unit seam (which wall a lookup answers) and the tool unit seam (the exact sentences), one test per wall
