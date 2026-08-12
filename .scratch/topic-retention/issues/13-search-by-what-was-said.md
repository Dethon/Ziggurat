# 13 — Search by what was said

**What to build:** A conversation can be found by something said in it, not only by what it is
called. This is the way into the archive: once six months of conversations are behind a filter,
remembering the title is not how anyone finds them.

Search returns topics rather than positions within them, because the sidebar's question is
which conversation, not which message.

**Blocked by:** 12 — Topic search moves to the server.

**Status:** ready-for-agent

- [x] Each topic has a searchable document holding its text, maintained as messages are
      appended.
- [x] Searching matches content as well as name, and returns topics.
- [x] Results span both the ordinary and the archived ranges.
- [x] A topic's searchable document expires on the same clock as the topic and is refreshed on
      the same write, so it is purged with it.
- [x] The migration builds documents for topics that already exist.
