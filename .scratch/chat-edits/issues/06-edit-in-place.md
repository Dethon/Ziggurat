# 06: Edit a message in place, kept as a version

Status: ready-for-agent
Blocked by: 04
Spec: ../spec.md (What a person can do, Versions)

## What
- **Hub.** `EditInPlace(topicId, entryId, text)`, which runs no turn.
  - A **user message** gets a new entry with the new text, the same attachments and the same timestamp. The tail after it is copied with new entry ids. The old message and tail become a version of the slot before it.
  - A **reply's** text is replaced as follows. The new version keeps the turn's tool call and result entries, drops text and reasoning from the earlier assistant entries of the turn, and puts the edited text in the last assistant entry. The tail after the reply is copied as above.
- **Client.** Save in the edit box (ticket 05's for user messages; a reply gets the same box with Save alone).

## Acceptance
- Store and hub tests for both roles. The model's view afterwards holds the edited text and paired tool calls.
- A client test of the reply's edit box.
