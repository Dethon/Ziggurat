# 10: Metrics and Lemonade per household

**What to build:** Metric events carry the household and roll-ups are kept per household; Lemonade's use is counted as events per household: speech seconds transcribed, characters spoken, embeddings computed. Spec: `../spec.md` ("Stores and metrics").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** ready-for-agent

- [ ] Every metric event names its household and the dashboard's roll-ups can be read per household
- [ ] A transcription, a spoken reply and an embedding each count against their household
