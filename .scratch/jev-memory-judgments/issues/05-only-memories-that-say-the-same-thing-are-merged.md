# 05 — Only memories that say the same thing are merged

**What to build:** The pair relation and the first refusal a merge has ever had. After cosine clustering, one request per cluster carries its memories by index and one four-way choice per pair (`same`, `updates`, `distinct`, `unrelated`, criteria as in the probe script); a cluster over `pairs.maxClusterMemories` is judged on the members nearest its centroid. The connected components of the `same`/`updates` links, singletons dropped, are what the merge model is called with. `MemoryDreamingService` applies a `Merge` or `SupersedeOlder` only when all its sources sit in one linked component of that pass; otherwise it is refused, logged by id and published. A cluster Jev did not answer for goes to the merge model as cosine made it and is applied unvetoed — today's behaviour, by decision. Independently of Jev, a `Merge` with blank merged content is refused and its sources survive.

**Blocked by:** 02, and `.scratch/jev-skill-preload/issues/02`

**Status:** resolved

- [x] A cosine cluster of {Madrid, Valencia-moved, sister-in-Sevilla, brother-in-Bilbao} reaches the merge model as one group of the first two; the siblings reach it not at all.
- [x] A merge decision naming one memory from each of two unlinked components is refused, both survive, and the refusal is published with the ids.
- [x] A `SupersedeOlder` over an `updates` pair is applied.
- [x] With the contract answering absence, the consolidator's calls and the applied decisions are identical to today's — pinned against the existing consolidator tests.
- [x] A `Merge` with empty or whitespace merged content deletes nothing, with Jev on and with it off.
- [x] A cluster of 30 asks about 12 memories and leaves 18 untouched this pass.
- [x] The pair questions reference the state by index and carry no memory ids the model could retype.
- [x] Spec: `.scratch/jev-memory-judgments/spec.md` § C — the pair relation.
