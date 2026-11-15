# 08: Reroll on another model, and replies say their model

Status: ready-for-agent
Blocked by: 07
Spec: ../spec.md (Turns that rewrite)

## What
- **The model is recorded.** Every stored reply message records the model that answered (`Model`, the turn's resolved model, after the whitelist decided). History projects it on the reply.
- **Client.**
  - "Reroll with…" opens a list of the agent's patchable models. The reroll sends that model as the turn's config patch. The topic's picker is left unchanged.
  - A reply whose model differs from the agent's own shows the model's short name in its footer.

## Acceptance
- A test shows that a patched turn's reply records the patched model, and that a refused patch records the agent's own.
- A client test of the model menu.
