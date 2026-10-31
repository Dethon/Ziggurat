# 09: Shared stores keyed by household

**What to build:** Every shared store's keys carry the household id: topics and their search documents, space indexes, push space subscriptions, schedules and outposts, so a store can never answer one household with another's data. Per-person stores stay keyed by user id. Spec: `../spec.md` ("Stores and metrics").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** ready-for-agent

- [ ] Two households' topics, schedules, space subscriptions and outposts never appear in each other's lists
- [ ] A search over topics returns only the caller's household's
