# 02 — A forget by query deletes everything the search returned

**Status:** needs-triage

**Where it was found:** writing the memory family of the behavioural eval, 2026-08-19
(`.scratch/behavioural-eval-harness/issues/14-memory-correction.md`). It is a reading of the code
rather than an observed incident, and it is why `Tests/Eval/Fixtures/EvalMemory.cs` fakes the store
instead of using the real one.

**What happens.** `MemoryForgetTool.ForgetBySearch` embeds the query, calls
`IMemoryStore.SearchAsync(..., limit: 100)` and deletes every result, filtered only by `olderThan`
and `maxImportance`. `RedisStackMemoryStore.SearchAsync` with an embedding runs a k-nearest-
neighbour query: it returns the *closest* hundred memories, with no relevance floor and no
threshold. Nearest is not the same as related.

**So:** a user with fewer than a hundred memories who says "olvida lo del piso" loses all of them,
and the tool reports `affectedCount` without anybody having asked. The agent's own behaviour is
correct in that scenario — it queries for the thing it was told to forget — so nothing in the
agent's contract catches this, and nothing in the reply says what went.

**What the eval does about it today.** The eval's fake store matches lexically, so its "the fact
beside it survived" assertion is a property of the fake and not of the deployment. That is
deliberate — ADR-0030's spec puts memory retrieval quality out of the eval's scope — but it means
the suite must not be read as evidence that this is safe.

**What a fix might look like** (not decided): a relevance floor on the search the forget tool
performs, a cap far below 100, or a confirmation step when a query would delete more than one
memory. A fix belongs with a test against the real `RedisStackMemoryStore` on `MemorySearchFixture`,
where the vector index actually exists.
