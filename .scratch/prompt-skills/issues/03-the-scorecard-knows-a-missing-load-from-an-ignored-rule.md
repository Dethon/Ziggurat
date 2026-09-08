# 03 — The scorecard knows a missing load from an ignored rule

**What to build:** Eval harness only, deterministic, runs unarmed. A load is an ordinary call: scenarios require it through the existing call expectation and permit it through the existing permission, never by default. When a scenario reddens, its per-scenario failure names whether a required load was missing or every required load happened and a checked behaviour did not. The scorecard carries that kind per scenario and per claim, and the dump shows it beside the recording. The load tool's name is the framework's, so this needs nothing from ticket 02.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] A recording with no load call against a scenario that requires one yields a failure whose kind says the skill was not loaded; the same recording with the load present and a behaviour check failing yields the other kind.
- [x] A load outside a scenario's permitted set is an unnecessary call, as any other tool is.
- [x] The scorecard's per-scenario and per-claim rows carry the failure kind, and the dump shows it beside the recording.
- [x] Spec: `.scratch/prompt-skills/spec.md` § Eval.
