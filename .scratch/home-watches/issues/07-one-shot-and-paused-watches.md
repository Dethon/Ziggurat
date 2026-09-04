# 07 — One-shot and paused watches

**What to build:** Asked "tell me when the washing machine finishes", the agent writes a watch with `once: true`; the rendered automation ends by turning itself off, so it fires once, and afterwards `watch.json` reads `enabled: false` and `status.json` says spent. Asked "stop warning me about her sugar tonight", the agent writes `enabled: false` on the existing watch and the automation is off; writing `enabled: true` turns it back on. Listing tells spent watches from paused ones, and the guide tells the agent to remove spent watches when it lists them or is asked.

**Blocked by:** 03 — A home-action watch.

**Status:** ready-for-agent

- [ ] Over the fake client: `once` appends a final self turn-off targeting the watch's own automation entity, for every effect kind; `enabled` false and true call the automation on/off; `enabled` reads back from the entity state; `spent` is true only for a `once` watch that is off.
- [ ] The guide teaches `once`, pausing, resuming and cleanup; claims declared.
- [ ] Eval scenarios: the washing-machine one-shot (watch count up, `once` on the automation), pausing tonight (count unchanged, automation off).
- [ ] Spec: `.scratch/home-watches/spec.md` § The mount, § Rendering the automation.
