# 07 — A large cluster is judged whole, in one pass

**What to build:** A cluster over `pairs.maxClusterMemories` was judged on its members nearest the centroid and the rest waited for a later night — and a night with no merges ends the loop, so the residual could wait indefinitely while every decision naming it was refused. The consolidator now cuts an oversized cluster into chunks of the cap, in order of distance to the centroid, and asks one request per chunk in the same pass: every member is judged, no request carries more than the cap's pairs, and a link across two chunks is found once the merges have shrunk the cluster. A chunk Jev did not answer for goes to the merge model as cosine made it, chunk by chunk.

**Blocked by:** 05

**Status:** resolved

- [x] A cluster of 30 asks three requests of 12, 12 and 6, the twelve nearest the centroid first, and every member reaches the merge model in its chunk.
- [x] With the judge absent, a cluster of 15 goes to the merge model as two chunks and both are mergeable groups.
- [x] Spec: `.scratch/jev-memory-judgments/spec.md` § C — the pair relation.
