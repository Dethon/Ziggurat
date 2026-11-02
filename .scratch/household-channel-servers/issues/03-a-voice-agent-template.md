# 03: A voice agent template, and a refused voice turn is spoken

**What to build:** `Domain/Prompts/Agents/nabu.md`, a template with the `voice` prompt section. It
carries the home Nabu's spoken-reply tuning without the home's own servers (Home Assistant, music),
and names the kit's servers the way the Jonas template does, reaching only those the household set
up. An owner adds it on the Agents page; its id is `nabu`, so the voice server's default agent id
addresses it unchanged. It is not created for a new household by default.

A turn refused from an attach-only channel gets a short reply instead of none, which the voice server
speaks. This covers an agent the household does not have (with "add a voice agent on the Agents page"
for voice), a suspended household and a disabled person. Spec: `../spec.md`.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] The template passes the template conformance tests (`PromptDocumentsTests`), names shared servers by address and the kit's by name, and its prompt snapshot is committed
- [ ] A household's `nabu` made from the template answers that household's voice turn (round trip)
- [ ] A voice turn for an agent the household lacks gets the spoken refusal, not silence (the round trip, attach-only)
- [ ] Suspended household and disabled person refusals are replied on an attach-only channel too
- [ ] The Agents page offers the template, and the owner can name it
- [ ] Run the eval's voice family on the template's agent once the prompt is written (changed tier does not select templates)
