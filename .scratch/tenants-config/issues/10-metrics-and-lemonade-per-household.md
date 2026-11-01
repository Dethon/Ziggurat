# 10: Metrics and Lemonade per household

**What to build:** Metric events carry the household and roll-ups are kept per household; Lemonade's use is counted as events per household: speech seconds transcribed, characters spoken, embeddings computed. Spec: `../spec.md` ("Stores and metrics").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** done

- [x] Every metric event names its household and the dashboard's roll-ups can be read per household
- [x] A transcription, a spoken reply and an embedding each count against their household
- [x] A household-migration step re-keys today's roll-ups into the home household

Notes: `MetricEvent.TenantId` (null is the deployment's own); keys `metrics:{family}:{household|deployment}:{date}`,
`metrics:errors:recent:{scope}`, `metrics:scopes`; health members `{household}:{service}`; every read takes
`?household=`; step `MetricRollups`. Not yet stamped, so counted as the deployment's: dreaming's events and
embeddings (it knows user ids only — 06 runs it per household), Jev's judgments billed off `JudgmentCaller`
(preload, exec screen, modal judge — 07), the memory forget tool's embedding, and shared servers' own errors.
The household filter UI is 4c's. `.claude/rules/observability.md`, "Households".
