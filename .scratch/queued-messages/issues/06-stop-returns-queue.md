# 06: Stop returns queued messages to their senders

**What to build:** Stop ends the running reply as today and empties the queue. Every queued message goes back into its own sender's composer on that topic, merged the way Edit merges.
- **Shared topics.** This holds for every sender, so a member whose message was queued when another member pressed Stop gets theirs back rather than having it run or vanish.
- **One device per person.** A person's returned messages land on exactly one of their devices: the first page that takes them.
- **No page open.** A sender with no page on the topic finds them in the composer the next time they open it.
- **Nothing runs** after the stop.

**Blocked by:** 05.

**Status:** ready-for-agent

- [ ] After a stop the queue is empty, each sender's items are held for them in order, and no further turn is emitted (hub seam).
- [ ] Each sender is told their items are waiting. A take hands them over once, and a second take, from any of their devices, gets nothing (hub seam).
- [ ] A page takes returned items when told while showing the topic, and on every topic open, and merges them into the composer (client seam).
- [ ] An installed page from before, which ignores what Stop returns, still stops the reply (hub seam).
- [ ] E2E: two members on a shared topic each queue a message, one presses Stop, and each finds their own text in their composer with no further turn run.
