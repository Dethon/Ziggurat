# Browse tabs

Status: ready-for-agent

## Problem Statement

The agent browses one page and the previous one ceases to exist. A browse session holds a single
live page, so reading a second URL navigates the first away — and with it every ref stamped there.
Worse than vanishing, the refs lie: both ref counters restart per page, so `i-1` from the first
page silently resolves against the second page's `i-1` and the model is shown the *wrong picture*
(or acts on the wrong element) with no signal anything is amiss. Two browses issued in parallel
interleave on the one page and can corrupt each other's extraction. Comparing two pages — a
product against a product, a chart against the article citing it — is the ordinary case and is
impossible without a fresh browse between every look. And a link that opens a new tab creates a
page nothing holds or closes: it leaks, while the action answers with a stale diff of the page
left behind.

## Solution

A browse session holds up to three tabs, and a ref finds its own. Browsing keeps recent pages
alive side by side; every ref is stamped with a session-unique number, so `view_image` and
`web_action` route each ref to the tab that issued it, whichever that is. The model never sees a
tab — it names what it wants and the want finds its page. Browsing a URL already open refreshes
that tab in place; a fourth distinct page quietly closes the tab longest untouched, and a ref
whose tab is gone or renumbered refuses by naming exactly what to do — snapshot again, or browse
a named URL again. A popup joins the pool like any browsed page and the action that spawned it
answers from it. See `docs/adr/0034` (and `docs/adr/0033`, which speaks in the same terms).

## User Stories

1. As a person asking the agent to compare two product pages, I want it to look at both pages' photos in one turn, so that the comparison is real rather than reconstructed from memory of the first page.
2. As a person whose question spans an article and a source it links, I want the agent to move between the two pages without losing its place in either, so that cross-referencing does not cost a re-read each hop.
3. As a person watching the agent browse, I want a link that opens in a new tab to actually take the agent there, so that login flows and document viewers work instead of silently going nowhere.
4. As the model, I want a ref from an earlier page to refuse rather than resolve against whatever page came later, so that I am never shown the wrong picture or click the wrong element without knowing.
5. As the model, I want to fetch a picture from a page I browsed three calls ago, so that looking at something new does not destroy my way back to something old.
6. As the model, I want `web_action` to act on the page whose ref I passed, so that I do not have to track which page is "current" to act correctly.
7. As the model, I want `web_snapshot` and `back` to address the tab I last touched, so that the ref-less operations follow my attention the way a person's browser follows theirs.
8. As the model, I want browsing a URL that is already open to refresh that page in place, so that the documented stale-ref recovery — browse the page again — does not duplicate the page or evict a sibling.
9. As the model, I want that reuse to match the address I asked for even when the site redirected it, so that recovery works with the URL I actually know.
10. As the model, I want a ref superseded by a newer snapshot of a still-open tab to tell me to snapshot or browse the current page again, so that I refresh instead of abandoning the page.
11. As the model, I want a ref whose tab was closed to name the URL it belonged to, so that I know exactly what to browse again rather than guessing which of several pages died.
12. As the model, I want a stale element ref and a stale image ref to refuse through the same two walls, so that one recovery story covers both namespaces.
13. As the model, I want ref shapes to stay the rule that says which tool a handle belongs to, with element refs finally spelled the way every document claims (`e-1`), so that the shape rule has no exception.
14. As the model, I want a popup to become the page my next call addresses, so that the action that opened it answers from where the site actually took me.
15. As the model, I want two browses issued in parallel to land on two tabs, so that neither call's result is corrupted by the other.
16. As the model, I want looking at an old tab's picture to refocus that tab, so that "the tab I last touched" is a single rule with no exceptions to memorize.
17. As a person paying for the conversation, I want tabs to stay invisible — no tab list, no tab handles in any envelope — so that browsing does not grow a third namespace or a per-turn tab inventory to pay for.
18. As an operator, I want the number of live tabs capped, so that one busy conversation cannot hold unbounded Camoufox pages.
19. As an operator, I want the tab cap and the session idle timeout to be ordinary settings on the browse server, so that a deployment can tune them without a code change.
20. As an operator, I want a session that is actively acting and snapshotting to not be idle-pruned mid-use, so that a long form-filling workflow cannot lose its browser under it.
21. As an operator, I want popup pages owned by the session that spawned them, so that they stop leaking until reconnect.
22. As a developer, I want the eviction and expiry story to stay one idle clock plus one LRU rule, so that no two lifetimes on one session can differ invisibly.
23. As a developer, I want the ref-range bookkeeping bounded and dying with its session, so that the registry cannot grow without limit.
24. As a developer, I want snapshot and image fetch serialized against navigation on the same tab, so that a mid-navigation read cannot answer with a half-replaced DOM.

## Implementation Decisions

- **A browse session holds up to three tabs; least-recently-touched is evicted.** Touch is any
  tool call routed to a tab — `view_image` included. Eviction closes the page. The cap and the
  session idle timeout (today hard-coded) become settings in the browse server's own
  `appsettings.json` — generic tunables, no compose entry, per the configuration rules.
- **One clock.** The session-wide idle timeout survives unchanged and is refreshed by every routed
  call (fixing the current gap where only navigation touches it). Tabs have no clock of their own;
  they die individually only by eviction. Two lifetimes on one session was rejected in ADR 0033
  and stays rejected.
- **Tabs are invisible.** No tab handles, no tab list, nothing new in any envelope or prompt.
  Routing is carried entirely by refs.
- **Every ref is session-unique.** Each namespace (element, image) keeps its own monotonic counter
  in session state, handed to the stamping scripts as a starting number. A number is never reused
  within a session. Element refs keep their wipe-and-restamp-per-snapshot lifecycle — only their
  numbers stop repeating. The element stamp is normalized to `e-1` (today the code emits `e1`
  while every document says `e-1`): one shape rule, letter-dash-number, across both namespaces.
  The stamp spelling, the ref-stripping rule and the not-found copy move together.
- **A ref routes to the tab that stamped it.** `web_action` and `view_image` act wherever their
  ref lives. The ref-less operations — `web_snapshot` and `back` — target the current tab: the tab
  the last call of any kind touched. Per-tab history makes `back` natural.
- **`web_browse` reuses before it opens.** Each tab remembers both the URL it was asked to load
  and the URL it landed on; a browse matching either exactly reloads in that tab (restamping its
  refs with fresh numbers) and makes it current. Anything else opens a new tab, evicting under the
  cap. Parallel browses of different URLs therefore get different tabs and genuinely run in
  parallel.
- **A popup is adopted as a tab.** It joins the pool, counts against the cap, becomes current, and
  the action that spawned it answers from it — closing today's leak and stale-diff answer.
- **Stale refs refuse through two walls, and each names its recovery.** The session keeps a small
  bounded registry of ref range → URL, with tombstones surviving eviction. *Superseded* — the tab
  is open but a later snapshot or an in-tab navigation renumbered it — says to snapshot or browse
  the current page again. *Closed* — evicted or expired — names the URL the ref belonged to and
  says to browse it again. Both walls join the existing refusal taxonomy; wording stays distinct
  from every existing refusal, per the every-refusal-names-its-wall rule.
- **Locking**: a per-tab lock serializes navigation, action, snapshot and image fetch on one tab
  (snapshot and fetch are unserialized today); tab-pool mutation (create, reuse, evict, adopt)
  happens under a session-level gate.
- **Unchanged**: cookies stay context-wide (tabs share the one browser context, so nothing about
  cookie behavior moves); images are annotated only by `web_browse`; the browse envelope, the MCP
  bridge, the read-image store and hydration are untouched — a routed fetch produces the same
  protocol image blocks as today.
- Glossary and decision record already exist: **tab**, and the widened **browse session**,
  **element ref** and **image ref**, are in `CONTEXT.md`; the decision is `docs/adr/0034`. The
  web-browsing rules file gains its tabs section as part of implementation, when the constraints
  describe shipped code.

## Testing Decisions

A good test asserts what a caller observes — the result a tool call answers, which page a fetch or
action lands on, the exact refusal sentence — never pool internals, lock state or call order.

**Four seams, all existing. No new seams.**

1. **Session/tab management** (existing unit seam around the session manager). The pool policy in
   milliseconds, no browser: same-URL reuse against requested and final URLs, LRU eviction at the
   cap, touch-on-any-call and current-tab tracking, monotonic counters never reusing a number, the
   tombstone registry answering superseded versus closed, and the idle clock refreshed by every
   routed call. Prior art: the existing session-manager idle/prune tests.
2. **Tool seam** (existing unit tests driving the tools over a faked browser contract). The two
   new refusal sentences, each distinct from every existing one; partial success surviving
   routing; the every-refusal-reads-differently property extended to the new walls. Prior art:
   the `view_image` tool tests.
3. **String seam** (existing unit tests). The `e-1` normalization where it is pure string work:
   the ref shape rule, the strip regex, entry shapes.
4. **Browser integration** (existing integration tests, real Camoufox). The honestly
   browser-dependent claims: refs routing across two live tabs, an action landing on the tab its
   ref names, popup adoption with the action answering from the popup, renumbering after an
   in-tab snapshot, and reuse across a redirect. Prior art: the page-image measurement and
   cross-origin probe tests.

Refusal wording is unit-tested at whichever seam produces it, one test per wall. No eval scenario:
the mechanism is unit- and integration-testable end to end, and no prompt claim has been declared
about when a model should open or revisit a page.

## Out of Scope

- **Tab handles or a tabs list for the model.** Tabs stay invisible; a `t-` namespace was
  considered and declined.
- **Refs surviving their tab.** A closed or renumbered tab's refs refuse; they are not remapped
  onto the reloaded page.
- **A per-tab idle clock.** One session clock; eviction is the only per-tab death.
- **Annotating images on action-driven navigation.** Images are still catalogued only by
  `web_browse`; an in-tab navigation leaves the tab's image refs superseded until the page is
  browsed.
- **Per-conversation cookie isolation.** Cookies remain context-wide across all sessions; a known
  pre-existing property this feature neither fixes nor worsens.
- **A memory limit on the Camoufox container.** The tab cap is the bound; a compose-level limit is
  a separate operational decision.
- **Restoring evicted tabs from history.** Browsing the URL again is the recovery; nothing replays
  a closed tab's state.

## Further Notes

Risks accepted knowingly, recorded in `docs/adr/0034`:

- Three live Camoufox pages per active session instead of one; the cap is the bound and it is a
  setting.
- Touching an old tab through `view_image` refocuses it, so a mid-workflow image peek moves what
  `web_snapshot` and `back` address next. Predictable — the last thing touched — but a behavior
  the model must absorb.
- A fourth distinct page silently evicts the least-recently-touched tab; its refs refuse with the
  URL to ask for again rather than warning beforehand.
- Element refs change spelling on the wire (`e1` → `e-1`); old transcripts' refs were already dead
  across sessions.

The decision record is `docs/adr/0034`, which ADR 0033 is phrased in step with. Glossary terms
live in `CONTEXT.md` under Web browsing: **tab** is new; **browse session**, **element ref** and
**image ref** were widened.
