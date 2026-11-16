# 04: The queue on every page

**What to build:** The queue is the topic's, so every page that sees the topic shows the same queue.
- **Devices.** The person's other devices show their chips.
- **Shared topics.** A member reading a shared topic sees what others queued, each chip named by its sender and read-only.
- **Catch-up.** A page that opens the topic, resumes its stream or reconnects asks for the queue and shows it as it is.
- **Privacy.** A private topic's queue reaches only those who see the topic: its owner and whoever sees all.

**Blocked by:** 03.

**Status:** done

- [x] Every queue change reaches the topic's audience by the same rule as its stream, and a private topic's queue reaches only its owner's and the sees-all groups (hub seam).
- [x] Asking for a topic's queue is refused "No such conversation." for one the caller may not see (hub seam).
- [x] A page catches up on the queue on topic open, on resume and on reconnect (client seam).
- [x] Other people's chips carry their names and no actions (client seam).
- [x] E2E: a second page on the same topic shows the same chips as the sending page.

**Notes from the implementation:**
- `GetQueue(topicId)` now answers a `QueueChangedNotification` (topic, items, `Revision`, space), the push's own record, so a page dispatches it as `QueueChanged` and the one reducer keeps whichever was read later. Nothing called it before, so no installed page binds the old shape.
- Already true at the hub before this ticket, now pinned: every push resolves the topic's audience as it is told (`QueueNews` reads the topic each time), so a share or an unshare mid-reply moves the queue's news with the stream's; `GetQueue` is gated by `SessionToReadAsync` ("No such conversation." to a member on a private topic, answered to sees-all). Both were confirmed failing with the behaviour removed.
- Already true on the client: other people's chips named and without actions (`QueuedSendTests`, `QueuedWithdrawalTests`); the catch-up tests pin it again on a fetched queue.
- Catch-up: `QueueEffect` asks on every `SelectTopic` and on `BecameLiveAgain` with a topic open; `StreamResumeService` asks on every resume, beside the pending approval. A topic open on a live stream therefore asks twice; both answers go through the revision rule.
- `GetQueue` on a topic the hub holds no session for answers ungated (as `SessionHeldToAsync` does for every read); such a topic has no stream and so no queue.
- Not covered: after an unshare, a member's message already queued in the now-private topic still runs; their page drops the topic and its chips with it.
- E2E: the person's other device (a second page in the sending page's context) opens the topic after both chips exist, so only the catch-up can show them; it was red with the client's catch-up dispatches removed. That page skips `SelectAgentAsync`, whose overlay dismissal turned down the held approval and let the queue run before the chips were read.
