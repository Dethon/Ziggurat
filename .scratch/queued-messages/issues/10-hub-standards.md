# 10: The hub's queue code reads as the rest of the repo does

**What to build:** No behaviour change. Bring the hub half of the queue (`McpChannelSignalR`: `StreamService`, `QueueDispatcher`, `PersonTurns`, `QueueNews`, `QueuedMessages`, `ChatHub`'s queue calls) in line with `.claude/rules/dotnet-style.md` and clear the review's smells, so ticket 12's race fixes land on a simpler shape.

**Blocked by:** 09.

**Status:** done

- [x] The per-topic queue state `StreamService` keeps in parallel dictionaries (queue, stream id, dispatching, steers, running-for, returned, stopped) is one per-topic object under the same lock; the repeated "get or create the list" and pending-count bumps are one place each; `Withdraw` and `Steer` share their take-out step.
- [x] A stream is named by a small type rather than a bare `long` beside its topic id; `Withdrawal`/`Vouching` become result types in the shape of the repo's `RewriteOutcome` (no `withdrawal.Message!`); a steer's refusal is not a `Withdrawal`.
- [x] Names say what they do: `Senders(...)`, `Finish(...)` (one job per method, not three by which arguments are null).
- [x] Time comes from `TimeProvider` (`QueuedMessage`'s time, `PersonTurns`' message times, the queue revision's seed).
- [x] `IReadOnlyList` for returned collections; no record holds a mutable dictionary.
- [x] `PersonTurns` and `QueueNews` are registered once and injected into `ChatHub` and `QueueDispatcher`, `QueueNews` taking its logger in its constructor (`WebChatCompositionTests`-style real composition test stays green).
- [x] Every refusal's words live in `TopicRefusals` (the dispatcher's "This message could not be sent.", the not-live answer used as a withdrawal refusal).
- [x] New comments in these files say why, never what.
- [x] Hub, monitor and client filters green under two concurrent processes; the queue E2E collection green.

**Notes from the implementation:**
- One commit per step, tests green after each: refusals into `TopicRefusals`, result types, `StreamKey`, the per-topic object with `Finish` split, DI, `TimeProvider`, then comments and the rule.
- **Per-topic state.** It is `TopicQueue` (`Services/TopicQueue.cs`), one per topic, in one dictionary under `_streamLock`. It has two lifetimes:
  - `Running` (a `RunningQueue`: the stream's `StreamKey`, the queue, the heads being dispatched, steers, `RunningFor`, the pending count) is dropped with the stream.
  - `Returned` and `LastStopped` (a `StoppedStream`, a class that keeps its own dictionary) live until taken or until `Forget`.
  - An entry with nothing left is removed. Lookups never create one.
  - `Returned` is one list filtered by account, because a per-topic dictionary keyed by `Account?` would throw on a null key where the old tuple key did not.
  - The pending count moved in too. It is nullable, so that "decrement when absent gives 0" stays exactly as `AddOrUpdate` had it.
  - The stream's `ConcurrentDictionary` state (channels, buffers, prompts, tokens, audiences) stays where it was. It is read without `_streamLock`.
- **`Finish` split.** It is now `Enqueue`, `Release` and `CompleteStream`. Each takes the lock once and calls `GiveUpPlace`, the shared tail that starts steers, takes the head or ends the stream. The end-of-stream push still goes out after the lock (`EndedStream`, `Announce`).
  - `Enqueue` now mints the `QueuedMessage` from the stream service's clock and takes the sender and the turn message.
  - `QueueNews.TellReturnedOrLogAsync` takes senders.
- **Results.** These follow `RewriteOutcome`: `Withdrawal` is `Withdrawn`/`WithdrawalRefused`, `Steering` is `Steered`/`SteerRefused`, and `Vouching` is `Vouched`/`NotVouched`. The renamed pieces:
  - `Withdraw` and `Steer` share `RefusalToTake` and `TakeOut`. This replaces `Senders` and `RefusalFor`, with refusal precedence unchanged.
  - `PersonTurns.NotLiveError`, `SignedOutError` and `UnvouchedError` are now `TopicRefusals.NotLive`, `SignedOut` and `Unvouched`. The dispatcher's failure is `TopicRefusals.NotSent`.
- **DI.** `PersonTurns` (taking `TimeProvider` and `ILogger<PersonTurns>`) and `QueueNews` (taking its logger) are registered in `AddChatSignalR`, the one place the shipped host and the hand-built test hosts share. The hub drops `ISessionAccounts` for the two of them. Test hosts register `TimeProvider.System`.
  - `ChatHubCompositionTests` resolves them, the hub (through `IHubActivator`) and the dispatcher from the real `ConfigureChannel`. It was confirmed red with the `PersonTurns` registration removed.
  - Hand-built hubs take `Fixtures/HubParts`.
  - `ChatHubNotLiveTests`' warning check now listens on `PersonTurns`' logger, since that is where the warning is logged.
- **Not changed.** These are the lock shapes, `HeldForSteer`'s per-chunk lock (ticket 12), revision bumps (a stop with only steers or heads in dispatch still bumps nothing), and `ChatHub`'s other `DateTimeOffset.UtcNow` reads (the cancel's timestamp, a fork's), which the ticket did not list.
- **`channels.md`.** It got identifier swaps only, to keep merges with the client branch small.
- **Left as they were.** The hub's own refusals that are not about the queue (`Session not found…` and the `Unvouched*Error` constants for spaces, agents and people) stay out of `TopicRefusals`; the client never reads them.
- **Merging with the client branch.** The constructors of `StreamService` (gains `TimeProvider`), `ChatHub` (`PersonTurns` and `QueueNews` replace `ISessionAccounts`) and `QueueDispatcher` changed, and so did `StreamService.Enqueue`'s signature. Any hub test written on another branch needs `HubParts` at the merge.
- **Verified.**
  - Hub, monitor and client filters: 1502/1502 in each of two concurrent processes.
  - The real-composition suites (`ChannelReceiveContractTests`, `McpServerContractTests`, `MetricsRegistrationContractTests`, `McpChannelConnectionCreateConversationTests`): 163/163.
  - The queue E2E collection: 3/3, on images the fixture rebuilt from this checkout.

