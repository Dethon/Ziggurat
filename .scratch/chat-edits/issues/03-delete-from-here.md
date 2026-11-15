# 03: Delete from here, with the message actions menu

Status: ready-for-agent
Blocked by: 02
Spec: ../spec.md (What a person can do, Who may do what, Mirrored topics, History changed)

The first end-to-end slice.

## What
- **Hub.** `DeleteFrom(topicId, entryId)` refuses when:
  - the person may not continue the topic;
  - the topic is streaming;
  - the topic is mirrored;
  - the guard fails ("This conversation changed; reload it.").

  On a reply's entry id it cuts from the first entry after the user message before it, so the hidden tool calls go too.
- **Mirrored marker.** `TopicMetadata.Mirrors` is set by `ConversationFactory` for review copies and voice topics. For older topics it is derived, as the spec says.
- **Attachment files.** Files that no remaining message or version references are deleted.
- **Other pages.** `OnHistoryChanged(topicId)` goes to the topic's audience. The client reloads the open topic's history when it is told.
- **Client.**
  - The actions button and menu sit on `ChatMessage.razor`'s footer, as a tap target. The icon is a new glyph in `Icons.cs`.
  - The menu is hidden while streaming and on the streaming bubble.
  - Delete asks for confirmation, then dispatches. On success the store drops the cut messages.
  - Fork-only topics show only Fork, which arrives with ticket 09, and Copy.

## Acceptance
- Unit tests cover the hub's refusals and the reply-cut resolution.
- A client test shows the menu's items for each case (own topic, shared, sees-all, mirrored, streaming).
- An E2E test deletes a reply's turn. A second page on the same topic redraws without it.
