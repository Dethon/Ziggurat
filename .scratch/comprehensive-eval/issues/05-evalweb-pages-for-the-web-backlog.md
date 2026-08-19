# 05 — EvalWeb pages for the web backlog

Status: resolved

Two additions, offline boundary intact:
- A page past the browse default limit, with the asked-for fact at the end: witnesses
  `web.partial-content-is-fetched-once` (fetch the rest once, then answer) and gives the written
  half of `web.raw-content-is-never-dumped` a page long enough to tempt.
- An inline-JS autocomplete field on a new page: witnesses `web.type-reacts-and-fill-sets`.
`web.back-is-an-action` stays exempt: no turn shape forces a return over remembering the page.

## Answer

Done. Two pages, offline boundary intact, both proven against the real browser in EvalWebTests:

- `/cronica/fiestas` — a deterministic 24-day chronicle past one default browse, with the raffle
  total only in the closing paragraph and absent from the snippet. Scenario "a truncated page is
  fetched once more and answered from" cites `web.partial-content-is-fetched-once`, ceiling 4;
  its four-sentence written bound also carries the written half of raw-content-is-never-dumped,
  whose exemption flips to guard.
- `/agenda/apuntarse` — an inline-JS autocomplete where the hidden activity id is set only by a
  suggestion's own click handler, so a value merely written bounces without a code. Scenario "an
  autocomplete is typed into rather than filled" cites `web.type-reacts-and-fill-sets`, requiring
  the type action by name.

One thing the fixture work surfaced: every web_action re-stamps refs in document order, so an
action that removes elements shifts every later ref — the fixture test re-snapshots after the
pick, exactly as the tool description tells the model to.

2026-08-19, later — Two corrections from the first armed run. Playwright's fill dispatches an
input event, so the input-driven suggestions opened for a filled value and type-vs-fill was
indistinguishable at the page: the list is now driven by keyup, which only real keystrokes
produce, pinned by `FillingTheActivityField_OpensNoSuggestionsAtAll`. And the chronicle turn,
phrased as research, went whole to a worker — the booking scenario's own lesson — so it now says
"abre..." like booking does. After the fixes: chronicle 3/3, autocomplete 3/4 with one worker
tolerated and the ceiling absorbing the delegation reflex the exemptions already record.
