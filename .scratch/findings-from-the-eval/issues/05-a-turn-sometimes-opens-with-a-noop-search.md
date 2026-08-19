# 05 — A turn sometimes opens with a `web_search` for "noop"

**Status:** resolved

**Where it was found:** the behavioural eval, 2026-08-19, on a Home Assistant scenario that has
nothing to do with the web ("pon el aire del salón a 22 grados"). Two runs of two.

**What happens.** The first tool call of the turn is
`web_search {"query":"noop","maxResults":1}`. The work that follows is correct — the temperature is
set, the reply is one spoken sentence — so the call changes nothing except that it happened. It
appears on whichever scenario is running when the model does it, which is what makes it worth
recording rather than attributing to the scenario it landed on.

**What it costs.** One provider round trip, one search API call per occurrence, and the latency of
both, on a turn that never needed the web. In this deployment the search would go to Brave and be
billed.

**Why the eval does not fail on it.** `ScenarioChecks` drops a search whose query is exactly "noop"
before the permitted-set and ceiling checks, with the reason written beside the code. Counting it
would turn one arbitrary scenario per run red for the model clearing its throat, which is noise
rather than a regression. The call is still listed in every failure dump.

**What to look at** (not decided): whether it is the model, the provider's tool-priming, or
something in how the tool list is presented. It started being visible when the websearch server was
hosted for every eval scenario, which is only when the eval had a `web_search` to call — the
behaviour may well predate that everywhere else the agent runs with web tools.

## Comments

2026-08-19 — The cost is fixed; the cause stays open. `WebSearchTool` now answers a query that
trims to the literal word "noop" — the same definition `ScenarioChecks.IsWarmUpProbe` uses —
without calling the search client: status `noop`, empty results, and a message telling the
model to continue with the user's request. The Brave call and its latency drop out; what
remains of an occurrence is one provider round trip, which nothing tool-side can remove. The
call still appears in recordings and failure dumps, so if the tic changes shape or frequency
the eval will show it. Whether it is the model or the provider's tool-priming is unanswered —
isolating that means A/B runs against providers, which is eval spend for a question that no
longer costs anything in production.
