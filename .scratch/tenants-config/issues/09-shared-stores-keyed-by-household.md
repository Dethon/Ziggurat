# 09: Shared stores keyed by household

**What to build:** Every shared store's keys carry the household id: topics and their search documents, space indexes, push space subscriptions, schedules and outposts, so a store can never answer one household with another's data. Per-person stores stay keyed by user id. Spec: `../spec.md` ("Stores and metrics").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** done

- [x] Two households' topics, schedules, space subscriptions and outposts never appear in each other's lists — outposts have no store any more (4a moved them onto tunnels, `TunnelledOutposts`, already keyed by tenant), so there is nothing of theirs to key; custom agents are ticket 11's
- [x] A search over topics returns only the caller's household's
- [x] A household-migration step re-keys each of these stores into the home household, re-runnable with a dry run (`ScheduleHouseholds`, `PushSpaces`, `TopicHouseholds`; the topics' step drops the old `idx:topics` and makes `idx:household-topics`)

Notes:
- Key shapes put an `@` after the store's prefix (`schedule@{tenant}:{id}`, `schedules@{tenant}`, `schedules-due` of `{tenant}:{id}`, `push:space@{tenant}:{slug}`, `topic@{tenant}:…`, `topics@{tenant}:{agent}:{slug}`, `topicdoc@{tenant}:…`) so the migration's old-shape patterns can never match a household's key.
- The conversation pointer `topic-of:{agent}:{chat}:{thread}` stays household-free (its value is `{tenant}:{topicId}`): the agent appending to a history knows only the conversation. Histories stay keyed by conversation.
- A turn acting for nobody names no household: it writes no schedule, and a topic it opens is kept under `none` and seen by nobody. `TopicViewer` no longer shows a topic recording no household; the voice guest's topic now records the guest's household.
- The chat hub's SignalR space groups are keyed by household too (`space:{tenant}:{slug}`…), since two households' "default" would otherwise hear each other's topic news.
