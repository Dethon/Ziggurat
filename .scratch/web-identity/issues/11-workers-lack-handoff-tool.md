# 11: Workers don't get the handoff tool

**What to build:** A handoff only ever comes from a conversation a person is in. Workers carry their parent's conversation context, so the browse server cannot tell their calls apart; instead the agent leaves `web_login` out of a subagent's tool set. Workers still browse `asMe` and reach the tabs they share with their parent. Spec: `../spec.md` ("Handoff").

**Blocked by:** 06

**Status:** done

- [x] A spawned worker's tool set lacks `web_login` while keeping the other web tools
- [x] The parent's tool set is unchanged
- [x] Tested where a subagent's tools are projected from its parent
