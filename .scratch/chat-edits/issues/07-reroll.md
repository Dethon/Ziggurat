# 07: Reroll the last reply

Status: ready-for-agent
Blocked by: 04
Spec: ../spec.md (Turns that rewrite)

## What
- **Wire.** `ChannelMessageNotification.RerunOf` (an entry id) travels to `ChannelMessage` and to the turn's user message.
  - If `send_reply`'s hand-built dictionary is affected, the spelling follows `ChannelProtocol`.
- **`RedisChatMessageStore`.**
  - When the request is a rerun of the trailing entry, it leaves that entry out of the history it provides.
  - When storing, it does not append the request message again.
  - A failed rerun marks the stored message failed in place, and keeps the retry working.
- **Hub.** `Reroll(topicId, configPatch?)`:
  - refuses as in ticket 03, and also when the last reply is an error, since Retry covers that;
  - puts the reply aside as a version of the slot after the last user message;
  - sends `OnHistoryChanged`;
  - sends the turn with the message's text and attachments.
- **Tool warning.** As in ticket 05.
- **Client.** Reroll in the last reply's menu.

## Acceptance
Unit tests for each tail shape:
- a completed turn;
- a turn with tool calls;
- a failed turn, where Retry still replaces;
- a stopped turn;
- a reroll that is itself stopped, which leaves the "No reply" placeholder and its switcher.

E2E: reroll, see 2/2 on the reply, switch to 1/2.
