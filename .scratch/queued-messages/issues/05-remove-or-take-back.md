# 05: Remove or take back a queued message

**What to build:** Each of the person's own chips offers Remove and Edit.
- **Remove** takes the message out of the queue and deletes its uploaded files that nothing else holds.
- **Edit** takes the message back into the composer, text and files, and out of the queue. Sending it again queues it at the end. Its text goes before whatever the composer already holds, joined by a blank line, and its files return as ready attachments.
- **Who may.** Only the message's sender may do either.
- **Too late.** If the message's turn started first, the person is told "already sent".

**Blocked by:** 03.

**Status:** ready-for-agent

- [ ] Another member's remove or take-back is refused (hub seam).
- [ ] A remove or take-back racing the dispatch either succeeds or answers "already sent", never both (hub seam).
- [ ] A removed message's upload is deleted, and a taken-back one's is kept (hub seam).
- [ ] Edit merges text and files into the composer as described, and the chip disappears (client seam).
- [ ] Only the reader's own chips offer the actions (client seam).
