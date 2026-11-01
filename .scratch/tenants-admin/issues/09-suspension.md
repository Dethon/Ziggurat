# 09: Suspension

**What to build:** The operator suspends and resumes a household from the list. While suspended, its sessions are refused at the session check, its turns and fires are refused by the agent with a message where a person is waiting, and its tunnels are refused and closed. Nothing is deleted. Spec: `../spec.md` ("Suspension").

**Blocked by:** 02

**Status:** done

- [x] A suspended household's people cannot sign in and see a clear message (the session check answers a page "Household suspended" and anything else a 403 with the notice; a turn on an open connection, Telegram or voice is answered `ConversationGroup.SuspendedAnswer`)
- [x] Its schedules, watches and Telegram messages start no turn (refused per message from the agent's view, a fire on the errors feed alone)
- [x] Its servers' tunnels are closed and refused (closed `Suspended` on the announcement, refused `Suspended` at verify, back-calls 401)
- [x] Resuming restores everything (nothing deleted; readers told at once; servers reconnect by themselves)

Not stopped (noted in `.claude/rules/admin.md`): the owner's own `/accounts/*` pages, and nightly dreaming on the household's key.
