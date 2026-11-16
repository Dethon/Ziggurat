# 10: The hub's queue code reads as the rest of the repo does

**What to build:** No behaviour change. Bring the hub half of the queue (`McpChannelSignalR`: `StreamService`, `QueueDispatcher`, `PersonTurns`, `QueueNews`, `QueuedMessages`, `ChatHub`'s queue calls) in line with `.claude/rules/dotnet-style.md` and clear the review's smells, so ticket 12's race fixes land on a simpler shape.

**Blocked by:** 09.

**Status:** ready-for-agent

- [ ] The per-topic queue state `StreamService` keeps in parallel dictionaries (queue, stream id, dispatching, steers, running-for, returned, stopped) is one per-topic object under the same lock; the repeated "get or create the list" and pending-count bumps are one place each; `Withdraw` and `Steer` share their take-out step.
- [ ] A stream is named by a small type rather than a bare `long` beside its topic id; `Withdrawal`/`Vouching` become result types in the shape of the repo's `RewriteOutcome` (no `withdrawal.Message!`); a steer's refusal is not a `Withdrawal`.
- [ ] Names say what they do: `Senders(...)`, `Finish(...)` (one job per method, not three by which arguments are null).
- [ ] Time comes from `TimeProvider` (`QueuedMessage`'s time, `PersonTurns`' message times, the queue revision's seed).
- [ ] `IReadOnlyList` for returned collections; no record holds a mutable dictionary.
- [ ] `PersonTurns` and `QueueNews` are registered once and injected into `ChatHub` and `QueueDispatcher`, `QueueNews` taking its logger in its constructor (`WebChatCompositionTests`-style real composition test stays green).
- [ ] Every refusal's words live in `TopicRefusals` (the dispatcher's "This message could not be sent.", the not-live answer used as a withdrawal refusal).
- [ ] New comments in these files say why, never what.
- [ ] Hub, monitor and client filters green under two concurrent processes; the queue E2E collection green.
