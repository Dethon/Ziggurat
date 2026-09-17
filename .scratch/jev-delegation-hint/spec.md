# Jev delegation hint — deferred

Status: needs-info

Grilled 2026-09-18 and deferred. Third of the five Jev uses named in
`.scratch/jev-skill-preload/spec.md`.

## Why it is deferred

The case for a per-turn "this looks like work for a worker" hint rested on `SubAgentPrompt.cs`'s
record that the model ignores the when-to-delegate guidance (parallel parts, heavy work) both as
prose and as the tool's description. What the record also says is that behaviour was adequate
either way, and the latest full scorecard (deepseek, 2026-09-17) has every delegation scenario
green — the guarded behaviours (a single call done in place, conversation-bound work kept)
already hold. There is one worker profile, so "which worker" is not a question.

The only plausible cost of not delegating is a parent context that fills with tool results a
worker could have carried, making every later turn of that conversation dearer and slower. Nothing
measures that today. A hint whose benefit cannot be measured would be a standing finding, not a
feature — the same reason those two bullets carry no claim.

## What unblocks it

Ticket 01: the measure. When the number says something, the design is the skill-suggestion
cookbook's shape — Jev judges per turn whether the request has several independent parts or is
heavy, and whether it is a single action or bound to the conversation; when heavy and neither of
the latter, one line rides on the turn: "this looks like work for a worker; ignore if it is not".
It would reuse the client and contract from the first spec and want a claim plus scenarios of its
own.
