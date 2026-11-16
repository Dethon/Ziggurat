# 05: Remove or take back a queued message

**What to build:** Each of the person's own chips offers Remove and Edit.
- **Remove** takes the message out of the queue and deletes its uploaded files that nothing else holds.
- **Edit** takes the message back into the composer, text and files, and out of the queue. Sending it again queues it at the end. Its text goes before whatever the composer already holds, joined by a blank line, and its files return as ready attachments.
- **Who may.** Only the message's sender may do either.
- **Too late.** If the message's turn started first, the person is told "already sent".

**Blocked by:** 03.

**Status:** done

- [x] Another member's remove or take-back is refused (hub seam).
- [x] A remove or take-back racing the dispatch either succeeds or answers "already sent", never both (hub seam).
- [x] A removed message's upload is deleted, and a taken-back one's is kept (hub seam).
- [x] Edit merges text and files into the composer as described, and the chip disappears (client seam).
- [x] Only the reader's own chips offer the actions (client seam).

**Notes from the implementation:**
- `RemoveQueued(topicId, queuedId)` and `TakeBackQueued(topicId, queuedId)` → `ReturnedMessage(Id, Content, Attachments, ConfigPatch)`; refusals are `TopicRefusals.AlreadySent` ("This message was already sent.") and `TopicRefusals.NotTheSenders`, a topic the caller may not see "No such conversation.".
- `QueuedMessageItem` is unchanged: the take-back's answer carries the references, so the push never hands other people's file ids to every page.
- Remove deletes only files kept under the topic's own conversation: `EnqueueMessage` does not check that a reference is the conversation's.
- The composer merge is `ReturnToComposer(topicId, messages)` (`ComposerStore`), for ticket 06's returned items too. The client ignores the config patch; the picker stays as the person left it.
- Until ticket 06, a stop empties the queue, so a Remove or Edit after a stop answers "already sent".

