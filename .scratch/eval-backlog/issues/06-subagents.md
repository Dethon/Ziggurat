# 06 — Subagents: the scenario that wants to delegate

Everything here rides one scenario, because everything here was blocked on the same thing: a
turn that reliably delegates. The armed evidence says research phrasing is that turn — "Busca…"
sent the whole chronicle task to a worker on both first-run attempts, and the booking did the
same on about half. So the turn is deliberately research-shaped where every other web scenario
learned to be imperative: "Busca en la web del Cuaderno de barrio los horarios del Museo del
Carmen y prepárame un resumen…".

The worker is canned with a real-looking answer (the museum's hours), so the scenario asserts
the parent's decision and prompt, never a second model's output:

- `heavy-work-is-delegated` — the declared delegation is required; any tool call by the parent
  itself is unnecessary (empty permitted set, delegation excluded from that check by design).
- `prompt-is-self-contained` — `Carries` demands the names only the parent saw (the museum, the
  site) inside the delegated prompt.
- `no-worker-is-named` — reply `NeverSays` the worker vocabulary.
- `success-criteria-are-stated` — judged: the delegated prompt says what a good result looks
  like, not just the topic.
- `answer-is-synthesised` — judged: the rubric embeds the canned worker answer verbatim and asks
  whether the reply answers from it in its own shape, at the length the question warrants.

If armed runs show the model doing the research itself instead (the parallel-parts finding
spreading to this shape), the five entries get the finding written down as their reason instead
— with the run evidence in this file.

**Status:** ready-for-agent

- [ ] Scenario written, exemption lines removed, coverage test green.
- [ ] Armed runs pass at policy — or the finding is recorded with evidence.
