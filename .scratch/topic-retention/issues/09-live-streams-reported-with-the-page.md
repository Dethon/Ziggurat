# 09 — Live streams are reported with the page

**What to build:** A page of topics says which of them have a reply in flight, so the client
stops asking about every topic one at a time. The agent-activity indicator keeps working.

The per-topic sweep is the second unbounded fan-out on start-up, after history loading. This
removes it rather than bounding it.

**Blocked by:** 04 — The topic list pages; 08 — History loads when a topic is opened.

**Status:** ready-for-agent

- [ ] A page of topics reports which of them have a topic stream in flight.
- [ ] The per-topic stream resume sweep is gone from start-up, agent switch and reconnect.
- [ ] Agent-activity indicators still show which agents are busy.
- [ ] A reply in flight on a topic in a later page is reported when that page is loaded.
- [ ] Taking over a reply already in flight still works, and a stale stream lease still does
      nothing.
