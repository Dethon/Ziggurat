# 05: Edit my message and re-run

Status: done
Blocked by: 04
Spec: ../spec.md (Turns that rewrite, Side effects)

## What
- **Client.** Edit on a user message turns the bubble into an edit box with Cancel, Save (ticket 06) and Save & re-run.
- **Hub.** `EditAndRerun(topicId, entryId, text)`:
  - refuses as in ticket 03;
  - puts the message and its tail aside as a version of the slot before it;
  - sends `OnHistoryChanged`;
  - then sends the new text as an ordinary turn on the person's stream, with the page's config patch. The original message's attachments are carried over.
- **Fire turns.** Not offered on a message a service started (spec: "A turn a service started can't be re-run").
- **Tool warning.** When the replaced tail called tools, the client confirms first and names the tools. The hub answers which tools those were (`ToolsCalledFrom(topicId, entryId)`, or carried on the history projection).

## Acceptance
- A hub test covers the order: the version is put aside, then `OnHistoryChanged`, then the turn's broadcast.
- An E2E test: edit an earlier message and re-run. The reply arrives, the switcher shows 2/2 on the user message, and switching back restores the old line.
