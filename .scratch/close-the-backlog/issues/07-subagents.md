# Subagents: the delegating scenario the family waited for

Status: resolved

## Answer

The reflex hypothesis was wrong, and the armed run said so: left to its own judgement the
model browsed the dead url itself — twice, https then http — and reported the timeout rather
than delegating. 0/3. `subagents.heavy-work-is-delegated` is therefore a **Finding** now,
beside `parallel-parts-are-delegated` and with the same shape of evidence: the rule is
stated and the deployment does not follow it.

The scenario keeps the four claims that are about a delegation's quality rather than the
decision, by delegating on the user's own instruction ("delégalo en un trabajador"): 3/4
armed, judged checks graded on every deterministic pass. One matcher lesson on the way: the
mentions check wants whole words, so "astronom" as a prefix spelling never matches
"astronomía".

The blocker on five claims was one missing scenario that reliably delegates. The chronicle
and autocomplete armed runs documented the shape that does: research phrasing ("Busca...")
sends this model to a worker so reliably that both scenarios had to tolerate it. This issue
turns that reflex into the required behaviour.

"research is delegated and answered from the worker's result": the url rides the history (only
the parent saw it), the turn asks for a two-part gather, the worker is canned with a real
answer.

- `subagents.heavy-work-is-delegated` — the declared delegation is required.
- `subagents.prompt-is-self-contained` — the delegation must carry `cuadernodebarrio.example`
  and `polideportivo`; a prompt saying "la web que me diste" reaches a worker with no history.
- `subagents.no-worker-is-named` — NeverSays on the reply.
- `subagents.success-criteria-are-stated` — judged, on the delegated prompt.
- `subagents.answer-is-synthesised` — judged; the rubric embeds the canned worker answer,
  because the judge's material carries the delegated prompt but not the worker's reply.

`subagents.parallel-parts-are-delegated` stays a Finding: this scenario asks for one gather,
not a parallel split, and the 2026-08-18 evidence about the split stands.
