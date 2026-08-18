# 14 — Memory correction and stale-memory removal

**What to build:** remembered facts are applied silently, corrected when the user corrects them,
and never narrated.

The scenario declares the remembered facts and they ride the turn the way recall already does, so
no embedding service participates and the no-cross-provider-fallback rule stays untouched. What is
asserted is what the reply used, which memory calls followed, and what the reply did not say.

**Blocked by:** 05, 09.

**Status:** ready-for-agent

- [ ] A scenario declares a recall block and it reaches the model as the memory contract promises.
- [ ] A declared fact is used in the answer without the reply mentioning memory, remembering or
      forgetting.
- [ ] A user correction results in the stale fact being removed and the new one recorded.
- [ ] A turn asking to forget something removes it and confirms without narrating the mechanism.
- [ ] Every scenario cites its claims and was demonstrated red once.
