# 03 — The glossary and the memory rule match the behaviour

**What to build:** Two documents currently describe the system as it was before 01 and 02. A
reader who trusts them is misled, and in the glossary's case the sentence is not merely stale but
inverted — it names as an exception the exact thing the boundary now forbids.

The **Lemonade chat host** glossary entry ends by saying that when the host is unreachable a turn
fails and nothing answers in its place, *except* the memory extraction that follows such a turn,
which falls back to the deployment's own extraction model. After 01 that is false: extraction does
not follow such a turn at all. The exception goes. Whoever wrote it wrote it deliberately, so the
edit is a reversal, not a tidy-up — leave the rest of the entry's shape intact.

The **memory architecture** rule attributes the extraction enqueue to the chat monitor. The recall
hook is what enqueues, and has been for some time; the error is caught here because 01 puts the
new gate into the very method the rule misnames.

Keep both edits to what 01 and 02 actually changed. This is not an invitation to rewrite either
document.

**Blocked by:** 01, 02 — it documents what they did, and the glossary sentence is still true until
01 lands.

**Status:** ready-for-agent

- [ ] The Lemonade chat host glossary entry no longer names memory extraction as an exception to
      "nothing answers in its place".
- [ ] The rest of that entry — the address-is-configuration point, the distinction from the
      deployment's own Lemonade, the with-no-address-it-does-not-exist rule — is unchanged.
- [ ] The memory architecture rule attributes the extraction enqueue to the recall hook rather
      than the chat monitor.
- [ ] The rule's surrounding contracts — the anchor's correctness argument, the local-embeddings
      rule, the marker cross-check — are unchanged.
- [ ] Neither document gains implementation detail; the glossary stays a glossary.
- [ ] ADR 0042 is referenced from wherever a reader would next ask "why".
