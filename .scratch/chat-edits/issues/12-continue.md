# 12: Continue a cut-off reply

Status: ready-for-agent
Blocked by: 10, 11
Spec: ../spec.md (Turns that rewrite: Continue)

## What
- **Hub.** `Continue(topicId)`:
  - refuses as in ticket 03, and also unless the last reply is `Interrupted`;
  - sends a turn whose user message is marked `Continuation` and carries the fixed instruction, spelled once in Domain.
- **Projection.** `Readable` never projects a continuation message, so the reply merges into the previous bubble. Memory extraction skips it.
- **The interrupted mark.** Once a continuation answers, the reply it continued is no longer projected as interrupted.
- **Client.** Continue in the last reply's menu when it is interrupted.

## Acceptance
- The model input ends with the interrupted reply and then the instruction.
- The page shows one bubble.
- E2E: stop a reply, continue it, and the bubble grows.
