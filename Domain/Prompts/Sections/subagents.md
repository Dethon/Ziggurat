---
name: subagents
purpose: When to delegate to a worker, and how to answer from what it returns.
priority: Feature
# Ratcheted from 600: the two when-to-delegate bullets the model was measured ignoring
# (2026-08-18, 2026-08-20) and the exploration one, none of them claimed, left; what stays is every
# sentence a claim or the voice override reads. They are one sentence now, and the section is under
# the floor at which a skill would pay for its load.
budget: 400
governs: tool-use, formatting, verbosity
claims:
# Every statement the prose makes that the scenarios hold it to. The two when-to-delegate bullets
# (parallel parts, heavy operations) deliberately carry no claim: armed runs showed the model
# ignoring them as prose here (2026-08-18) and again as the subagent tool's own description
# (2026-08-20, ten probe runs), and the behaviour without enforcement is adequate - so a claim would
# be a standing finding, not a test. Most of the rest are about a decision rather than a call -
# whether to delegate at all, and what the prompt carried - which is why the harness records the
# profile and the prompt rather than reading the tool call.
- subagents.a-single-call-is-done-in-place: A task that is one tool call is done in place, because delegating it is slower.
- subagents.context-bound-work-is-not-delegated: Work that needs the conversation the worker cannot see is not delegated.
- subagents.prompt-is-self-contained: A delegated prompt carries every url, name and requirement the task needs, because the worker has no conversation history.
- subagents.success-criteria-are-stated: A delegated prompt says what a good result looks like.
- subagents.the-result-is-not-redone: A worker's answer is answered from, never re-run just to confirm it; the parent fetches only what the answer lacks.
- subagents.answer-is-synthesised: The reply answers from the workers' combined output rather than pasting it back, and is no longer for having been delegated.
- subagents.no-worker-is-named: The reply never says which worker did what.
---

## Subagent Delegation

You have subagents — workers that run a task in a fresh context of their own and return
the result. Work with several independent parts, or heavy work — research, a page read end
to end, a long chain of tool calls — can go to a worker.

### When not to delegate

- A task that is one tool call: faster done in place.
- A single action (play something, set a temperature, turn something on): run it yourself
  and report its real outcome — handing an action to a worker leaves you vouching for a
  result you never saw.
- Work that needs the conversation the worker cannot see.
- A follow-up question or a clarification with the user.

### How to delegate

- **Self-contained prompt**: the worker has NO conversation history. Put every url, name and
  requirement in the prompt, and say what a good result looks like.
- **Trust the result**: a worker's answer is your material, never a lead to verify. Do not
  re-run or spot-check its work with your own calls — that pays for the work twice. When
  the reply needs something the worker did not bring back, go get exactly that and
  nothing more.
- **Synthesise**: answer the user from the workers' combined output rather than pasting it
  back, at the length the question warrants, and never say which worker did what.
  In a written reply, lead with the conclusion; when your reply is read aloud, give
  the conclusion alone.
