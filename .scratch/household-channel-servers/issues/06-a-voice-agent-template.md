# 06: A voice agent template

**What to build:** A third agent template beside Jonas and Sugary, for an agent that answers aloud:
- It carries the voice prompt section and the home Nabu's spoken-reply tuning, without the home's
  Home Assistant and music.
- It names the tenant kit's own servers by name, and reaches only the ones the household set up.
- Its id is `nabu`, so a household's voice server addresses it with no setting of its own.

An owner adds it on the Agents page, names it, and gives it custom instructions. A new household does
not get one by default. Spec: `../spec.md`.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] The template passes the template conformance tests: shared servers by address, the kit's own servers by name
- [x] Its prompt snapshot is committed
- [x] An owner can add it on the Agents page, name it, and its turns in chat carry the voice section
- [x] An agent made from it reaches only the own servers its household set up (turn-level household tests)
- [ ] (not run: it spends eval money; run it before relying on the template) Run the eval's voice family on the template's agent (the changed tier does not select templates)
