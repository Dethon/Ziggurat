# 0034 — A browse session holds tabs and a ref finds its own

Status: accepted
Date: 2026-08-26

## Context

A browse session is one Playwright page. Browsing a second URL navigates that same page, which
destroys the first page's DOM — and with it every ref stamped there, element and image alike.
Because both ref counters restart per page, the destruction is silent: `i-1` from page A resolves
against page B's `i-1` and answers with the *wrong picture*, the failure shape everything in ADR
0033 was built to avoid. Two `web_browse` calls issued in parallel are worse still: they interleave
navigation, annotation and extraction on one page, and the loser's markdown can be corrupted
mid-extraction rather than merely superseded.

Three smaller facts sharpen the problem. A link that opens a new tab (`target=_blank`,
`window.open`) creates a page in the shared context that nothing holds, observes or closes — it
leaks until reconnect, while the action answers with a stale diff of the page left behind. The
element-ref stamp is spelled `e1` in the code while every document says `e-1`, so the two
namespaces disagree about the one thing — shape — that is supposed to tell tools apart. And a
session's idle clock is only refreshed by navigation, so a conversation that spends half an hour
acting and snapshotting without navigating can be pruned mid-use.

Comparing pages is the ordinary case — a product against a product, a chart against the article
that cites it — and today it is impossible without re-browsing between every look.

## Decision

**A browse session holds up to three tabs, and a ref finds its own.**

**Tabs are invisible.** No tab handles, no tab list, nothing new in any envelope. The model names
a ref and the ref carries its routing; a third namespace beside `e-` and `i-` would be one more
thing to juggle for no capability the refs cannot express.

**Every ref is session-unique.** Each namespace keeps its own monotonic counter in session state,
handed to the stamping scripts as a starting number: page A stamps `i-1…i-5`, page B stamps
`i-6…i-9`, and a later snapshot of A restamps with numbers higher still. A number is never reused
inside a session, so a stale ref can *refuse* — the silent wrong-picture answer becomes
unrepresentable. Element refs keep their wipe-and-restamp lifecycle; only their numbers stop
repeating. The stamp is normalized to `e-1` while that code is open, one shape rule —
letter, dash, number — across both namespaces, as the documents have claimed all along.

**Restamping is about what replaces a page, not about every look at one.** An explicit
`web_snapshot`, a browse and any navigation restamp the whole document. The implicit capture a
non-navigating `web_action` takes afterwards *augments* instead: existing refs stay on the
document and only newly appeared elements draw fresh numbers. Restamping there would supersede the
refs the model is in the middle of using: a four-field form would die after its first fill, every
remaining field refusing as stale. Acting on a page is not the same as replacing it, and only the
second should cost the handles on it. The eval web fixtures cover it.

**A ref routes to the tab that stamped it.** `web_action` and `view_image` act wherever their ref
lives, whichever tab that is. The two ref-less operations — `web_snapshot` and `back` — target the
**current tab**: the tab the last call of any kind touched, `view_image` included, mirroring where
a person's attention just was.

**A snapshot composed with a browse names its page rather than following the current tab**, because
across two calls "current" is not stable. `web_browse(snapshot=true)` names the page the browse
landed on and refuses when that tab is gone, instead of falling back: a parallel browse can move
the current tab between the browse and its snapshot, and falling back would attach another page's
refs to this page's text — precisely the wrong-target pairing that naming a page prevents. The
browse re-notes its landed URL after processing, so a page that rewrites its own address mid-read
is still found.

**`web_browse` reuses before it opens.** A URL matching a live tab — by the address the model
asked for *or* the address that tab landed on, since redirects split the two — reloads in that tab
and restamps its refs; anything else opens a new tab. "Browse the page again", the documented
recovery for a dead ref, therefore refreshes in place instead of duplicating the page and evicting
a sibling. Parallel browses of different URLs get different tabs and genuinely run in parallel;
same-tab work is serialized by a per-tab lock that snapshot and image fetch now also take.

**Three tabs, least-recently-touched evicted.** Touch is any routed call. The cap and the
session's idle timeout become ordinary settings on the browse server; the idle clock stays one and
session-wide, refreshed by every routed call — tabs die individually only by eviction, because a
second, per-tab clock would be two expiry rules on one session, differing invisibly (the same
reasoning ADR 0033 used to refuse a longer clock for image refs).

**A popup is adopted as a tab.** It joins the pool, counts against the cap, becomes current, and
the action that spawned it answers from it — the leak is closed and the stale-diff answer with it.

**A stale ref names its wall, and there are two.** The session keeps a small registry of ref
range → URL, with tombstones surviving eviction. *Superseded* — the tab is open but a later
snapshot or an in-tab navigation renumbered it — says to snapshot or browse the current page
again. *Closed* — evicted or expired — names the URL it belonged to and says to browse it again.
A model told only "not found" cannot tell a page it can refresh from a page that is gone.

The URL a closed wall names is the address the model most recently asked that tab with, not the
one it landed on, so recovery works with the URL the model actually knows — reuse re-records it on
each browse. A popup is the one case with no asked-for address: it names where it landed, because
nobody asked for it at all.

The registry is bounded at 128 ranges and drops oldest-first, and that bound buys a third outcome
at the edge: a ref whose range has aged out routes as *unknown* and falls through to the current
tab's ordinary not-found. It is strictly weaker than either wall — no page named, no recovery —
though it still cannot resolve to the wrong target, since a number is never reused and so matches
nothing. This is accepted rather than solved: a session's three tabs hold far fewer than 128 live
ranges, so what ages out is deep history, and a ref that old has usually outlived the model's
interest in it. Raising the bound trades memory for how far back a dead ref can still name its
page.

## Consequences

- Three live Camoufox pages per active session instead of one. The container carries no memory
  limit today; the cap is the bound, and it is a setting.
- The session record grows: tabs, per-namespace counters, the ref-range registry, per-tab locks.
  The registry is bounded at 128 ranges and dies with the session; past that bound a dead ref
  loses its wall and falls back to an ordinary not-found.
- Restamping holds for what replaces a page, not for every capture of one: a non-navigating
  action's implicit capture must augment, or it supersedes the refs the model is mid-flow with.
- The composed browse-plus-snapshot is the one ref-less operation that does not follow the current
  tab, because between its two halves "current" is not stable.
- `view_image("i-1")` after browsing elsewhere refuses precisely instead of answering with another
  page's picture — and comparing two pages' images in one turn works, the case that motivated all
  of this.
- Re-browsing to refresh refs no longer costs a tab slot, but a fourth distinct page silently
  closes the least-recently-touched one; its refs refuse with the URL to ask for again.
- Touching an old tab through `view_image` refocuses it, so the next `web_snapshot` or `back`
  addresses it. Predictable — the last thing touched — but a model peeking at an old picture
  mid-workflow moves its own focus.
- Element refs change spelling on the wire (`e1` → `e-1`): snapshot stamping, the strip regex and
  the not-found copy move together, and old transcripts' refs were dead across sessions anyway.
- Cookies stay context-wide, images are still annotated only by `web_browse`, and no eval scenario
  is added — the mechanism remains unit- and integration-testable.
- ADR 0033 already speaks in these terms — a ref living in the tab that stamped it, the fetch
  going through the page that listed it — so the two records describe one design from two angles
  rather than one correcting the other.
