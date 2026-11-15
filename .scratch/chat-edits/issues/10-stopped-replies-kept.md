# 10: A stopped reply is kept

Status: done
Spec: ../spec.md (Cut-off replies)

## What
- **What is kept.** A turn the person stops now persists:
  - the user message;
  - the completed response messages, with tool calls paired with their results and any unpaired call dropped;
  - the text streamed so far, as an assistant message marked `Interrupted`.

  Find where the partial updates are available on cancellation: `ConversationGroup.StreamAgentTurn`, or the framework's invoked context.
- **The model reads it.** It stays in `ModelView`.
- **History.** It projects `IsInterrupted`. The client draws "Stopped" under the bubble.
- **Every channel.** This is the store's rule, so it covers Telegram's `/cancel` and a voice stop too.
- **The in-flight key** (ticket 02) is cleared only after the partial reply is persisted. Ticket 02 sets and clears it, so if 10 lands first, add the key here.
- **Docs.** Update `.claude/rules/channels.md` where it says "a stopped turn keeps nothing". Update `FailedTurnHistoryTests` too.

## Acceptance
- A stop mid-text keeps the partial text.
- A stop mid-tool-call keeps no unpaired call.
- A stop before any text keeps the user message alone.
- The next turn's model input carries the partial text.
