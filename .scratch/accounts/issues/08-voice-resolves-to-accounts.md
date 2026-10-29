# 08: Voice resolves to accounts; a guest acts as nobody

**What to build:** nabu acts as the person it recognizes and as nobody otherwise. A speaker profile is named after its user id and a conclusive match is that account; no match is the voice guest, a principal with no user, who can still control the house, set timers and ask questions but reaches no person's memory, logins or Sugary log. The enrollment script and the voice rules say so. Spec: `../spec.md` ("Channels and the principal", Voice).

**Blocked by:** 06

**Status:** ready-for-agent

- [ ] A matched speaker's turn carries their account
- [ ] An unmatched turn carries a principal with no user
- [ ] A guest turn can still call house, timer and question tools
- [ ] The enrollment script names a profile by user id
