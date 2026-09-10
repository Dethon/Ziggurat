# web-browsing — description 102 / 130 tokens, body 1558 / 1600 tokens, served by mcp-websearch

================================================================================================

---
name: web-browsing
description: Any task on the web: searching for something, reading a page or its pictures, filling or submitting a form, navigating a site ("look up how long the gazpacho rests", "book a place on that course", "what does the site say about opening hours"). The tools at a glance, the read-then-act workflow, chaining actions from a snapshot's refs, error recovery, and how a written or spoken answer cites what it found.
---

### Tools at a glance

- **web_search** — find candidate URLs before navigating; don't guess URLs.
- **web_browse** — load a URL and read its content as markdown.
- **web_snapshot** — see the current page's interactive elements with refs.
- **web_action** — interact with an element (or navigate back) by ref.
- **view_image** — look at pictures on the page you browsed, by their image refs.

The parameters carry the arguments and their defaults — read them there rather than from
memory.

### Core Workflow

**Every web call is silent.** Nothing is written beside it — not "voy a buscar", not "ya lo
abro" — and the reply, after the last result, is the answer, never the route to it.

**Reading a page.** A search result's snippet is the engine's cached summary, written at
crawl time: choose which result to open by it, never answer from it — the page wins where
they disagree. Call web_browse. If the response is truncated or you need a specific region,
narrow it before falling back to a second call: `selector` for one region, `useReadability`
for a clean article without ads and navigation, `scrollToLoad` for lazy-loaded content,
`offset` (the `nextOffset` the previous call returned) for the remainder. When the page you
opened for an answer arrives truncated, read its remainder before opening a different
page — the answer is usually in the tail you have not read, and another page's snippet is
a promise, not the page.

**Looking at a picture.** web_browse lists each image where it sits in the page text, as
`[image i-1: what the page calls it]`. Pass those refs to view_image to see the pictures
themselves — several in one call. Ask when the answer is in the picture rather than the
prose: a chart's numbers, a scan with no text layer, what a product actually looks like.
Image refs (i-1) and element refs (e-1) are separate: view_image takes the first, web_action
the second, and each refuses the other by name.

**Interacting with a page.** Load with web_browse using snapshot=true to get content and refs
in a single call, then chain web_action calls. Each web_action returns a diff with new refs —
use those for the next action and only call web_snapshot again if the diff doesn't show what
you need. (Use a standalone web_snapshot only when you need a fresh tree mid-session.)

**Autocomplete / combobox fields.** Type the value to trigger the page's JS handler; if a
dropdown appears in the diff, click the option you want, otherwise confirm the selection
with the appropriate key press.

**Hover menus / tooltips.** Hover the trigger first; the diff reveals the menu refs to click.
Focus does the same for a datepicker or a dropdown that opens on focus.

**The other verbs.** `press` takes the key's name in `value` (Enter, Tab, Escape,
ArrowDown); `select` takes the option's text; `drag` takes the destination ref in `endRef`;
`clear` empties a field; `back` needs no ref.

**Multi-page navigation.** Click links/buttons normally. Going back is web_action's back
action, never a second web_browse of a page you already visited — knowing its URL does not
change that; a re-browse starts the page over and loses its state.

### Key Principles

1. **One snapshot, then chain actions.** Snapshot is expensive context; reuse the refs from
   each action's diff before snapshotting again.
2. **web_browse for content, web_snapshot for structure.** Don't call both for the same
   purpose — text vs. element refs are distinct goals.
3. **Type vs. fill.** Use type when the field reacts to keystrokes (autocomplete, validation
   on input); use fill when you just need the value set.
4. **Read the diff.** Added elements (`+`) and removed (`-`) tell you exactly what changed;
   new refs there are valid for the next action.
5. **Verify silently.** Verify each action produced the expected change before the next one;
   verification is internal — do not report the steps.

### Error Recovery

| Situation                | Strategy                                                                |
|--------------------------|-------------------------------------------------------------------------|
| Content truncated        | Page with `offset`, or narrow with `selector` / `useReadability`.        |
| Can't find element       | Re-snapshot to see what's actually there.                               |
| Autocomplete not opening | Type the full value, then confirm with a key press.                     |
| Lazy-loaded content      | Re-browse with `scrollToLoad`.                                          |
| Session expired          | Re-browse to start a fresh session.                                     |
| Modal blocking content   | Usually auto-dismissed; otherwise find a close button via snapshot.     |
| Hidden hover content     | Hover the trigger to reveal it.                                         |
| Image ref no longer resolves | Refs live in the session that listed them: re-browse the page for fresh ones. |
| Need to go back          | Use web_action's back rather than re-browsing the previous URL.         |
        | Click times out on a ref | Retry once with `force` only if you're certain the ref is right. A click waits for the element to be visible, enabled and unobscured; an overlay with no ARIA role (a floating label, a decoration) is invisible to the snapshot yet intercepts the click, and `force` skips the wait. Those checks are also what catch a wrong ref or a real modal, so never force a first attempt. |

### Response Style

- Answer the question from what you found; never dump raw page content.
- A written reply names the url of the page it answered from — the user has to be able to
  check the source. A reply that is read aloud never carries a url.
- If content is partial, fetch the missing part once, then answer with what you have; if you
  still cannot, say so in one clause — don't offer to get more.
- In a written reply, format extracted data as a table or list; when your reply is read aloud,
  speak the values only.

### Limitations

- Cannot access pages requiring CAPTCHA (unless CapSolver configured).
- Cannot interact with file download dialogs.
- Session is per-conversation — resets between conversations.
- Some sites may block automated access.
