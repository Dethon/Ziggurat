---
paths:
  - "McpServerWebSearch/*"
  - "McpServerWebSearch/McpTools/**"
  - "McpServerWebSearch/McpPrompts/**"
  - "McpServerWebSearch/Modules/**"
  - "McpServerWebSearch/Settings/**"
  - "Domain/Tools/Web/Web*"
  - "Domain/Tools/Web/*Ref.cs"
  - "Domain/Tools/Web/Modal*"
  - "Domain/Tools/Web/ViewImageTool.cs"
  - "Domain/Tools/Web/BrowserFootprint.cs"
  - "Domain/Prompts/WebBrowsingPrompt.cs"
  - "Domain/Prompts/Sections/websearch_prompt.md"
  - "Domain/Prompts/WebBrowsingSkill.cs"
  - "Domain/Prompts/Skills/web-browsing.md"
  - "Domain/Contracts/IWebBrowser.cs"
  - "Infrastructure/Clients/Browser/BrowserSession*"
  - "Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs"
  - "Infrastructure/Clients/Browser/MainFrame*"
  - "Infrastructure/Clients/Browser/Modal*"
  - "Infrastructure/Clients/Browser/AccessibilitySnapshot*"
---

# Web Browsing Architecture

See also: `browsing-as-person.md` (logins, `asMe`, the handoff), `web-images.md` (image entries, `view_image`), `camoufox.md` (the browser container).

McpServerWebSearch exposes the `web_*` browse tools. The `websearch_prompt` (`Domain/Prompts/WebBrowsingPrompt.cs`) is the standing stub — the browser exists, a url comes from a search, no call is a probe — and the workflow, principles, error recovery and answer style are the `web-browsing` skill (`Domain/Prompts/WebBrowsingSkill.cs`, shipped through `AddSkills`; `docs/adr/0039`). The tools run over `PlaywrightWebBrowser`, a WebSocket to Camoufox. The accessibility snapshot assigns element refs (`e-1`, `e-2`, …) that `web_action` addresses; pages live per session with cookie persistence; cookie banners, newsletters and age gates are auto-dismissed.

**A page whose own script moved it onto an error is asked for once more.** A bot check answers 200, sends the page on, and has the server refuse that follow-up while leaving behind what lets the next visit through (Reddit's "blocked by network security" on a fresh session). `BrowsePageAsync` reads the settled document's status (`performance`'s navigation entry — the commit's response belongs to the page that left) and browses the caller's address once more; a second landing reports the error. `HttpStatus` is that settled status, not the commit's. A navigation the page's script started but the server has not answered is waited for (`MainFrameNavigationWatch`, up to 10s) before the page is read — read early, the browse extracted the check itself — and a stability reading torn down by a navigation counts as a change, not an error.

## Overlays

`ModalDismisser` runs on every navigation and closes an overlay in three steps, each costlier and run only when the one before came up empty: a button selector, a word in a control's accessible name, and — for an overlay both left standing — a judgment (`ModalJudge`, `Domain/Tools/Web`) over what its controls say, asked of Jev with `{ overlay_kind, controls }` and nothing of the page. A cookie wall is asked *reject* then *accept*, a confident reject taken first (fewest cookies); `none`, a pick under the bar, a stale click or a late answer leave the page exactly as before.

**A name is a label, and some controls are never offered.**
- A name (the control's whole `textContent`) is cut to `ModalJudge.MaxNameLength` before it leaves the page — past that it is the page's words, not a label — and its quotes are escaped: the criteria are prose the page contributes a substring of, and a name closing its own quote can write an instruction beside it.
- A control whose name reads as acting on the person's own data (delete, pay, buy, transfer, deactivate) is filtered out of the judge's list, so it can never be picked: container selectors are substring matches (a `confirm-modal` reads as a newsletter) and nothing on this path can undo a POST. A wall with no control left to offer is not asked.
- The dismisser keeps the uncut names, so the re-read before the click compares the name the page carries; the pick is clicked by index into the same locator its name was read from.
- The overlay predicate, the name script and the anchor guard are one JS constant each, so the three steps cannot disagree about which buttons are the wall's.

The judge and its bar, deadline and cap are the server's own `TypeSafe` / `Judgment` settings; an empty key is the feature off. **A turn addressed to the Lemonade chat host asks nothing** — the Jev client's rule, not this server's (`.claude/rules/mcp-hosting.md`). `web_browse` reads the turn off the call's `_meta`; it rides `BrowseRequest.Caller` through the dismisser to `ModalJudge.PickAsync`; the client answers `AbsenceReason.LocalTurn`, which the judge reads as `NotAsked`, not a miss — the wall is left to the model exactly as with no key.

**Every detected overlay is counted, and a wall that closed is not a miss.** `DismissAsync` answers one `ModalOverlayOutcome` per overlay the last pass detected — `selector`, `text`, `judgment` or `left-standing` — and the browser publishes each as a `ModalDismissalEvent` (dashboard: Web). Container selectors overlap, so one wall matches several patterns: after any dismissal the page is rescanned and a kind whose container is gone is dropped rather than counted as left standing, while one still there is a separate wall and goes to the judge. A judgment nobody asked reports no latency, so a keyless page cannot drag the average to zero. Metrics are why this server holds a Redis connection; it also carries the login handoff's state (`browsing-as-person.md`), never page images. The probe the questions were measured on (`.scratch/jev-modal-dismissal/probe/README.md`) is kept as `ModalJudgeJevTests`; change a question's wording only with that test green.

## Tabs

A browse session holds up to three live tabs, and a ref finds its own (`docs/adr/0034`). The tab protocol lives in `BrowserSessionManager`, four per-intent methods (`BrowseAsync` always supersedes the tab's refs; `OnRefAsync` answers the routing walls; `OnCurrentTabAsync` is the ref-less default; `OnUrlAsync` the composed-snapshot exception) plus pool lifecycle. A caller hands over the work and receives its result or a named wall (`TabOutcome<T>`); ordering, locks, stamp leases and closed-vs-disconnected never cross the seam, and the protocol is unit-tested against faked pages (`TabProtocolTests`). `PlaywrightWebBrowser` only carries pages around. Read the module top to bottom for the ordering — this file deliberately does not restate it.

**Tabs are invisible.** No tab handles, no tab list, nothing in any envelope or prompt — refs carry all routing. A `t-` namespace was considered and declined.

**Every ref is session-unique.** Each namespace (element `e-`, image `i-`) keeps its own monotonic counter in session state, handed to the stamping scripts as a starting number; a number is never reused within a session, which makes the silent wrong-picture answer unrepresentable. One shape rule — letter, dash, number — covers both (`ElementRef` / `ImageRef` in `Domain/Tools/Web`); the stamp spelling, the diff's strip regex and the not-found copy move together. A non-navigating `web_action`'s implicit capture **augments** rather than restamps — existing refs stay, only newly appeared elements get numbers — because a restamp superseded refs mid-flow and a four-field form died after its first fill (the eval web fixtures are the regression tests). An explicit `web_snapshot`, a browse and any navigation restamp the whole document.

**A ref routes to the tab that stamped it.** `web_action` and `view_image` act wherever their ref lives; the session's registry of ref range → tab answers. The ref-less `web_snapshot` and `back` address the **current tab**: the one the last call of any kind touched, `view_image` included (peeking at an old tab's picture refocuses it). `back` walks its tab's own history. One exception: the snapshot composed with `web_browse` (`snapshot=true`) names the page the browse landed on (`SnapshotRequest.ForUrl`) and refuses if that tab is gone — a parallel browse can move the current tab between the two calls, and falling back would attach another page's refs to this page's text.

**`web_browse` reuses before it opens.** A URL matching a live tab exactly — the address asked for *or* landed on, since redirects split them — reloads that tab in place and restamps its refs; anything else opens a new tab, evicting the least-recently-touched at the cap. Parallel browses of different URLs get different tabs and genuinely run in parallel.

**A popup is adopted as a tab.** `target=_blank` / `window.open` pages (popups of popups included) join the pool, count against the cap, become current, and the spawning action answers from the popup — its URL, content, fresh refs — not a stale diff of the opener.
- The browser reports a popup only once it says it is ready, which can land after the click settled, so a click that leaves its own page's URL and its target's surroundings unchanged waits up to `_popupGrace` (1.5s); the wait ends the moment a popup is adopted against that tab, so only a click that did nothing pays it.
- A popup reported after the grace is still adopted and made current: no call already working takes current back as it finishes, a ref-less call queued on the opener runs on the popup, and the next action on the opener discards it rather than answering from it.
- The grace is pinned on a fake clock in `TabProtocolTests`; a real-browser test waits for the adoption rather than asserting on the click's own answer, which a loaded machine reports past the grace (`PersonContextBrowseTests`).

**A stale ref refuses through one of two walls, each naming its recovery.** *Superseded* — the tab is open but a later snapshot or in-tab navigation renumbered it — says to snapshot or browse again. *Closed* — evicted at the cap — names the URL the ref belonged to (the last address the model asked that tab with; a popup adopted as `about:blank` names where it landed). The registry keeps tombstones past eviction and dies with its session — an expired session keeps the existing dead-session refusal, never a third wall. Refs are not remapped onto a reloaded page.

**The registry is bounded, and overflow degrades quietly.** `BrowserSession.MaxRanges` is 128, dropped oldest-first. A ref whose range aged out routes as *unknown* and falls through to the current tab's ordinary not-found — weaker than either wall, the one place the registry stops answering "which tab stamped this", but never the wrong target (a number is never reused). Raising the bound trades memory for how far back a ref can still name its page.

**One idle clock, one LRU rule.** The session-wide idle timeout is refreshed by every routed call, so an active session cannot be pruned mid-use; tabs have no clock and die individually only by eviction. The cap and timeout are generic tunables in the browse server's own `appsettings.json` (`Browsing:TabCap`, `Browsing:SessionIdleTimeoutMinutes`), no compose entry. Locking and lock ordering are the module's invariants, held by code and its tests rather than this file.
