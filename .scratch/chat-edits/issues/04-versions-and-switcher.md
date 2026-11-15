# 04: Versions are projected and switched

Status: done
Blocked by: 03
Spec: ../spec.md (Versions)

## What
- **History projection.** `GetHistory` projects `Versions { Index, Count }` on the first bubble of each slot holding more than one version.
  - Reroll slots sit on the reply.
  - Edit slots sit on the user message.
  - A slot whose active tail is empty, which happens after a stopped or failed reroll, projects a "No reply" placeholder that carries the switcher.
- **Hub.** `SwitchVersion(topicId, slotEntryId, index)`, with the same refusals as ticket 03, then `OnHistoryChanged`.
- **Client.**
  - A `‹ i/n ›` switcher in the bubble footer, built from two new glyphs.
  - A switch reloads the history.
- **Delete inside a slot** removes that version and shows the newest remaining one, as built in ticket 02.

## Acceptance
- Projection tests cover nested slots.
- A switcher component test.
- An E2E test once ticket 05 or 07 can make versions. It may be folded into 07's E2E.
