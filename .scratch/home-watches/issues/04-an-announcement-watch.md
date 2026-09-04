# 04 — An announcement watch

**What to build:** Asked "wake me if Laura's sugar drops under 60 at night", the agent writes a watch whose effect is an `announce` with the alarm bridge's `target` and `insistent` shapes and a text that may carry a template for the value, plus a night-time condition; the home's automation calls the existing voice-announce rest_command with that payload, so it rings in the bedroom until acknowledged with no agent involved. The alarm bridge document and the local compose Home Assistant configuration currently disagree on the announce payload shape; the rendering follows what prod runs and the two are reconciled in this ticket. The eval's fake home gains a glucose sensor with a state class.

**Blocked by:** 03 — A home-action watch.

**Status:** ready-for-agent

- [ ] Over the fake client: an `announce` effect renders the voice-announce call with text, target and insistent; a watch with an `announce` followed by `actions` renders them in that order.
- [ ] The announce payload shape in the rendering, the bridge document and the local compose configuration agree; the integration seed carries the same rest_command.
- [ ] The guide teaches when an announcement is the right effect (a fixed sentence, urgency, insistence) and that it needs no agent; claims declared.
- [ ] Eval scenario: the night-time glucose alarm above (insistent announcement, condition), declaring the watch count as its change.
- [ ] Spec: `.scratch/home-watches/spec.md` § Rendering the automation, § Provisioning and rollout.
