# 02 — A topic's last-write time is stamped server-side

**What to build:** A topic driven by voice or by a schedule is ordered by when it was last
written to, the same as one driven by a browser. Today only the browser records this value, so
a conversation with nobody watching sorts by when it was created however much is said in it.

Every retention decision that follows reads this one value, so it has to be true for every
topic before anything is keyed on it.

**Blocked by:** 01 — One topic store behind the interface.

**Status:** ready-for-agent

- [ ] Appending to a topic's history updates the topic's last-write time, whatever did the
      appending.
- [ ] A conversation with no browser attached is ordered by real activity rather than by
      creation.
- [ ] Ordering is correct without the browser writing the value.
- [ ] Covered against a real store with a fake clock.
