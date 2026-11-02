# 07: A refused turn on an attach-only channel is answered

**What to build:** When the agent refuses a turn that came from an attach-only channel (voice), it
answers the turn with a short refusal for the channel to speak, instead of nothing that leaves a
satellite waiting two minutes. The refusals are:
- an agent the household does not have, which for voice says to add a voice agent on the Agents page;
- a suspended household;
- a disabled person.

Each says what it says on the other channels. Spec: `../spec.md`.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A voice turn for an agent the household lacks is answered with the refusal, naming the Agents page (round trip, attach-only)
- [ ] A suspended household's and a disabled person's voice turns are answered with their refusals
- [ ] A channel that is not attach-only refuses as it does today
