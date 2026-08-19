# 03 — A factual question is sometimes answered from the search snippet

**Status:** resolved

**Where it was found:** the behavioural eval's web family, 2026-08-19
(`.scratch/behavioural-eval-harness/issues/15-web-workflow.md`, scenario "a page that contradicts
its snippet is believed").

**What happens.** The served site's museum page opens with "desde el 1 de agosto el museo abre a
las 10:30, no a las 9:00 como venía siendo habitual". Its search snippet still says "Abierto todos
los días de 9:00 a 20:00". Asked what time the museum opens, the agent searches, and on some runs
answers "abre a las 09:00 y cierra a las 20:00" without opening the page at all. Observed on two
of three runs once, and not reproduced on three later runs — call it a minority behaviour rather
than a constant one.

**What the contract says.** Nothing, on this point. `WebBrowsingPrompt` tells the model how to read
a page and to answer from what it found, and says to search rather than guess a url; it never says
a result summary is not an answer. Deleting the two sentences it does have changes nothing, and a
sentence saying a snippet is not a source was tried and reverted — it made no measurable
difference on four runs, and unmeasured prose is what the claim discipline exists to prevent.

**Why it matters.** A snippet is a summary written by somebody else at some other time. The failure
is silent, fluent and confidently wrong, and the eval only catches it because the page was authored
to disagree with its own snippet.

**What a fix might look like** (not decided): a rule with teeth about when a page must be opened,
or a tool-level change — a search result that carries a "summary, may be stale" marker rather than
a bare snippet. Whatever is tried, the scenario is the acceptance test, and it should be moved back
to a two-of-three threshold if it starts holding.

## Comments

2026-08-19 — Fixed at the tool level, since prompt prose was tried and measurably changed
nothing. `web_search` now carries the marker in both places the model reads: the tool
description says a snippet is the engine's cached summary and the page wins where they
disagree, and every success envelope opens with a `note` saying snippets may be stale and a
factual answer comes from opening the page. Pinned in
`Tests/Unit/Domain/Tools/Web/WebSearchToolTests.cs`. The museum scenario stays the acceptance
test at two of four; move it back to two of three once armed runs show it holding.
