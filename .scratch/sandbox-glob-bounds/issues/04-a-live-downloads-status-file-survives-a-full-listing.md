# 04 — A live download's status file survives a full listing

**What to build:** Globbing the media library shows what is downloading, even when the directory
holds more ordinary files than the response can carry.

The mount merges its disk walk with the rendered entries a live download owns, then applies the
response cap. Disk entries go first, so two hundred ordinary files push a live download's status
file out of its own listing and the agent cannot see the download at all. This is true today and
is not caused by the bounded walk, but the merge is being touched anyway to carry the walk's new
coverage flag and scanned count through, and leaving the drop in place would contradict the rule
this mount already follows everywhere else: a live download owns its path.

The overlay's entries are reserved first — there are at most as many as there are live downloads,
a handful — and the remainder of the response is filled from the disk walk, sorted for
presentation. The overlay enumerates a finite in-memory set and scans nothing, so the coverage
flag and the entry count come from the disk half unchanged.

**Blocked by:** 03 — the fields being carried through have to exist first.

**Status:** ready-for-agent

- [ ] A glob over a downloads directory holding more disk entries than the response carries still
      returns the live download's status file.
- [ ] The coverage flag and the scanned count in a merged result are the disk walk's own values.
- [ ] Truncation still reports correctly when the combined set exceeds the response cap.
- [ ] A listing with no live downloads is unchanged from today.
- [ ] The existing refusal rules on the mount are untouched.
