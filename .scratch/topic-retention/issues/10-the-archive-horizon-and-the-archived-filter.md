# 10 — The archive horizon and the archived filter

**What to build:** A topic that nothing has written to for six months stops appearing in the
ordinary sidebar. It is not deleted: an explicit filter lists archived topics, paged the same
way, and they open and read as they always did. Writing to an archived topic puts it back in
the ordinary list with no other action taken.

Archived is where a topic sits in the topic index, never something stored on it. There is no
archive verb, no unarchive verb and no background job. See ADR 0024, and `Archived` in
`CONTEXT.md`.

**Blocked by:** 04 — The topic list pages.

**Status:** ready-for-agent

- [x] A topic whose last write is older than the archive horizon does not appear in the ordinary
      list.
- [x] An explicit filter lists archived topics, paged the same way as the ordinary list.
- [x] Writing to an archived topic returns it to the ordinary list, with nothing else done to it.
- [x] Nothing anywhere stores whether a topic is archived.
- [x] Changing the horizon takes effect on the next read, with no migration or backfill.
- [x] The horizon is a setting in the retention policy block, six months.
- [x] Covered against a real store with a fake clock, on both sides of the boundary.
