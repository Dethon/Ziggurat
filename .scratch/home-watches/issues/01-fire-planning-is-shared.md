# 01 — Fire planning is shared

**What to build:** A prefactor. The way a schedule turns its `deliverTo` list into reply targets (parse `channelId[:address]`, group by channel, coalesce sub-addresses, a bare entry subsumes specific ones) and stamps a notification with its agent and origin moves out of the scheduling server into shared domain code, so the watch callback in ticket 05 composes its notification through the same function and the two cannot drift. While there, the schedule's `userId` reaches the notification (it is stored today but never put on the fire), and the message origin gains a **Watch** kind beside Schedule. Schedules behave exactly as before.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The schedule fire planner's target parsing and coalescing live in the domain layer with a name that says fire planning, and the scheduling server calls them; the existing planner tests pass unchanged against the moved code.
- [ ] A fired schedule with a `userId` carries it on the notification; a test drains a real inbox and sees it.
- [ ] A `Watch` message origin kind exists, carrying a watch id, and nothing in the monitor treats it as a schedule.
- [ ] Spec: `.scratch/home-watches/spec.md` § The callback.
