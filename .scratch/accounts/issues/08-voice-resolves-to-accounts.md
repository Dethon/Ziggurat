# 08: Voice resolves to accounts; a guest acts as nobody

**What to build:** Speaker profile folders are named by user id and a conclusive match is that user's principal. No match is the household guest: a principal with no user, no memory extraction, no person's logins or Sugary, and a shared review copy; house control, timers and questions still work. `scripts/enroll-voice.sh` and the voice rules say so. Spec: `../spec.md` ("Voice").

**Blocked by:** 06

**Status:** ready-for-agent

- [ ] A matched speaker's turn carries their user id (dispatcher tests)
- [ ] A guest turn carries no user and extracts no memory
- [ ] A guest's review copy is a shared topic
