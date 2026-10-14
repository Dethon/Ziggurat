# 12 — Full-stack E2E and switching her over

**What to build:** One end-to-end test on the compose stack: the app sends a meal photo, the real `sugary` agent answers through the Sugary channel, the meal is saved, it appears in `/sugary/meals` and the conversation appears as a WebChat topic. Then her phone moves from the standalone app to this one, once the eval tier shows the `sugary` agent no worse than the standalone app on the same meals, and her standalone log is imported.

**Blocked by:** 08, 10, 11

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] The E2E passes on the compose stack and its wait on the model is bounded by the provider, as in the existing chat E2E.
- [ ] The eval tier's scorecards for photo and video are no worse than the standalone figures, and are kept with the switch.
- [ ] Her standalone meals and foods are imported and spot-checked in the app and in `/sugary`.
