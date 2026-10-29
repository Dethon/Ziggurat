# 10: Memory follows the account

**What to build:** A person has one memory across Telegram, the chat client and voice. Memory entries and the personality profile are keyed by the principal's user id; recall and extraction read it and never the sender's display name; a turn with no user, such as a voice guest, recalls and extracts nothing. The memory rules follow. Spec: `../spec.md` ("Keyed by user id", Memory).

**Blocked by:** 06, 08

**Status:** done

- [x] A fact told on Telegram is recalled on the chat client by the same person and not by another (`ChatMonitorMemoryAccountTests`, through the monitor, the recall hook and the extraction worker against Redis; Telegram turns themselves carry an account only once 07 lands)
- [x] A guest turn recalls nothing and stores nothing
- [x] Dreaming still visits every user's memories

Notes for later tickets (`.claude/rules/memory-architecture.md` "Memory is a person's" has the detail):

- Memory is keyed by `Principal.Account.UserId` and nothing else: `IMemoryRecallHook.EnrichAsync` takes the turn's `Account?` (the conversation group passes `message.Principal?.Account`), and `memory_forget` reads the turn in flight's principal per call through `FeatureConfig.ConversationContextProvider`. `FeatureConfig.UserId` and `IAgentFactory.Create`'s `userId` are still the opening turn's sender and still feed the agent's prompt line and a worker's sender id; nothing in memory reads them.
- A turn with no account recalls and extracts nothing, so **Telegram turns have no memory until 07 stamps their account** — the intended state between tickets, not a regression (Sugary's channel turns go to the `sugary` agent, which has no memory feature).
- A fire (any `OriginKind`) recalls as the account its principal carries and is never extracted from. Before this, fires were extracted under their sender (`scheduler`, `watch`), so 16 finds those buckets in prod beside `household`; the spec only says where `household` goes, so 16 must decide theirs (they hold whatever was extracted from fire prompts, likely to be dropped).
- Keys keep their shape; no old key is read as a fallback. Until 16 runs, prod's memories under channel names are unreachable and the dashboard's `memory:byUser:` counts opaque account ids.
- The eval harness now stamps every turn `Principal(channel, eval-user, Account(eval-home, eval-user))`, person's turns included; a later ticket that keys something else by account gets the same id.
