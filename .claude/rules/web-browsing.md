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

McpServerWebSearch exposes the `web_*` browse tools over `PlaywrightWebBrowser` (a WebSocket to Camoufox). The `websearch_prompt` (`Domain/Prompts/WebBrowsingPrompt.cs`) is the standing stub; the workflow, error recovery and answer style are the `web-browsing` skill (`Domain/Prompts/WebBrowsingSkill.cs`, shipped through `AddSkills`; `docs/adr/0039`). The accessibility snapshot assigns the element refs `web_action` addresses.

**A page whose own script moved it onto an error is asked for once more.** `BrowsePageAsync` reads the settled document's status (`performance`'s navigation entry, not the commit's response) and browses the caller's address once more; a second landing reports the error. `HttpStatus` is that settled status. A navigation the page's script started is waited for (`MainFrameNavigationWatch`) before the page is read, and a stability reading torn down by a navigation counts as a change, not an error.

## Overlays

`ModalDismisser` closes an overlay on every navigation in three escalating steps — a button selector, a word in a control's accessible name, then a judgment of the controls (`ModalJudge`, `Domain/Tools/Web`) asked of Jev with `{ overlay_kind, controls }` and nothing of the page. A cookie wall is asked *reject* then *accept*, a confident reject taken first (fewest cookies). Anything but a confident pick of a listed control leaves the page exactly as before.

**A name is a label, and some controls are never offered.**
- A name is cut to `ModalJudge.MaxNameLength` before it leaves the page and its quotes are escaped: the criteria are prose the page contributes a substring of, and a name closing its own quote can write an instruction beside it.
- A control whose name reads as acting on the person's own data (delete, pay, buy, transfer, deactivate) is never offered to the judge, so it can never be picked; a wall with no control left is not asked.
- The dismisser keeps the uncut names for the re-read before the click, and clicks by index into the same locator its name was read from.
- The overlay predicate, the name script and the anchor guard are one JS constant each, so the three steps cannot disagree about which buttons are the wall's.

The judge's bar, deadline and cap are the server's own `TypeSafe` / `Judgment` settings; an empty key is the feature off. **A turn addressed to the Lemonade chat host asks nothing** — the Jev client's rule, not this server's (`.claude/rules/mcp-hosting.md`, "The Jev client holds the local-box rule and the bill"): `web_browse` passes the turn from the call's `_meta` as `BrowseRequest.Caller` to `ModalJudge.PickAsync`, and `AbsenceReason.LocalTurn` is read as `NotAsked`, not a miss.

**Every detected overlay is counted, and a wall that closed is not a miss.** `DismissAsync` answers one `ModalOverlayOutcome` per detected overlay (`selector`, `text`, `judgment`, `left-standing`), published as a `ModalDismissalEvent`; after a dismissal the page is rescanned and a kind whose container is gone is dropped, not counted as left standing. A judgment nobody asked reports no latency. This server's Redis carries metrics and the login handoff's state (`browsing-as-person.md`), never page images. Change a judge question's wording only with `ModalJudgeJevTests` green (`Tests/Eval/ModelChecks/`, an eval probe: it runs only with `ZIGGURAT_EVAL=1`, so a bare `dotnet test` skipping it is not green).

## Tabs

A browse session holds up to three live tabs, and a ref finds its own (`docs/adr/0034`). The tab protocol is `BrowserSessionManager`'s four per-intent methods (`BrowseAsync`, `OnRefAsync`, `OnCurrentTabAsync`, `OnUrlAsync`) answering `TabOutcome<T>` — a result or a named wall; ordering, locks and stamp leases never cross that seam (`TabProtocolTests`). `PlaywrightWebBrowser` only carries pages around. The module is the source for ordering and locking; this file does not restate it.

**Tabs are invisible.** No tab handles, no tab list, nothing in any envelope or prompt — refs carry all routing (a `t-` namespace was declined).

**Every ref is session-unique.** Each namespace (element `e-`, image `i-`) has its own monotonic per-session counter handed to the stamping scripts; a number is never reused within a session, which makes a silent wrong-target answer unrepresentable. One shape rule covers both (`ElementRef` / `ImageRef`); the stamp spelling, the diff's strip regex and the not-found copy move together. **A non-navigating `web_action`'s capture augments rather than restamps** — existing refs stay, new elements get numbers — because a restamp superseded refs mid-flow and a multi-field form died after its first fill (the eval web fixtures guard it). A `web_snapshot`, a browse and any navigation restamp the whole document.

**A ref routes to the tab that stamped it.** `web_action` and `view_image` act wherever their ref lives. The ref-less `web_snapshot`, `back` and `web_screenshot` address the **current tab**, the one the last call of any kind touched (`view_image` included); `back` walks its tab's own history. The snapshot composed with `web_browse` (`snapshot=true`, `SnapshotRequest.ForUrl`) names the page the browse landed on and refuses if that tab is gone, never falling back to another page's refs.

**`web_browse` reuses before it opens**: a URL matching a live tab's asked-for *or* landed-on address reloads it in place and restamps; anything else opens a tab, evicting the least-recently-touched at the cap.

**A popup is adopted as a tab** (`target=_blank`, `window.open`, popups of popups), counted against the cap and made current, and the spawning action answers from the popup. A click that changes nothing visible waits up to `_popupGrace` for one; a popup reported later is still adopted and made current, a ref-less call queued on the opener runs on it, and **the next action on the opener discards it rather than answering from it**. Real-browser tests wait for the adoption rather than assert on the click's answer (`PersonContextBrowseTests`).

**A stale ref refuses through one of two walls, each naming its recovery.** *Superseded* — the tab is open but a later snapshot or in-tab navigation renumbered it — says to snapshot or browse again. *Closed* — evicted at the cap — names the URL the ref belonged to. Tombstones outlive eviction and die with the session; an expired session keeps the dead-session refusal, never a third wall. Refs are not remapped onto a reloaded page.

**The registry is bounded, and overflow degrades quietly.** Past `BrowserSession.MaxRanges` ranges the oldest drop, and such a ref falls through to the current tab's ordinary not-found — weaker than either wall, never the wrong target.

**A hung page closes its tab.** A page whose script holds its thread answers no Playwright call, and `page.content()` and evaluate have no timeout of their own — one browse waited 35 minutes, holding its tab's lock and the turn. Page reads go through `PageReads` (a 60 s bound); its `PageUnresponsiveException` is answered by the tab protocol, which closes the tab (ending every call pending on it) and answers `TabOutcome.Unresponsive`. Every work also runs under `Browsing:WorkDeadlineMinutes` and the call's token (a person's stop reaches the server as that token, which Playwright's reads never look at); past either it is abandoned — its stamp lease returned, a later stamp refused — and its tab closed the same way. That deadline leaves room above the slowest browse that ends on its own (two captcha solves with their reloads already make 6 minutes), so lowering it cuts real work.

**One idle clock, one LRU rule.** Every routed call refreshes the session-wide idle timeout; tabs have no clock and die only by eviction. `Browsing:TabCap` and `Browsing:SessionIdleTimeoutMinutes` live in the browse server's own `appsettings.json`, no compose entry.
