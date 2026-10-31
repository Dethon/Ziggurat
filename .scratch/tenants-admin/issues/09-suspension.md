# 09: Suspension

**What to build:** The operator suspends and resumes a household from the list. While suspended, its sessions are refused at the session check, its turns and fires are refused by the agent with a message where a person is waiting, and its tunnels are refused and closed. Nothing is deleted. Spec: `../spec.md` ("Suspension").

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] A suspended household's people cannot sign in and see a clear message
- [ ] Its schedules, watches and Telegram messages start no turn
- [ ] Its servers' tunnels are closed and refused
- [ ] Resuming restores everything
