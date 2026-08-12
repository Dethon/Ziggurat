# 06 — Row previews come from a stored snippet

**What to build:** Each sidebar row shows a snippet of what was last said in that topic,
supplied by the server rather than computed from message history the client happens to hold.
Previews look the same as they do now.

Nothing a user can see changes here. This exists so that ticket 08 can stop loading every
topic's history without leaving every row blank.

**Blocked by:** 03 — The topic index replaces the scan.

**Status:** ready-for-agent

- [ ] A topic carries a truncated snippet of its last message, written on the same path that
      stamps its last-write time.
- [ ] Rows render the snippet from the topic rather than from loaded messages.
- [ ] The migration backfills snippets for topics that already exist.
- [ ] Snippet length is a setting in the retention policy block.
- [ ] Previews are unchanged to look at.
