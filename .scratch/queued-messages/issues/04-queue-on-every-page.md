# 04: The queue on every page

**What to build:** The queue is the topic's, so every page that sees the topic shows the same queue.
- **Devices.** The person's other devices show their chips.
- **Shared topics.** A member reading a shared topic sees what others queued, each chip named by its sender and read-only.
- **Catch-up.** A page that opens the topic, resumes its stream or reconnects asks for the queue and shows it as it is.
- **Privacy.** A private topic's queue reaches only those who see the topic: its owner and whoever sees all.

**Blocked by:** 03.

**Status:** ready-for-agent

- [ ] Every queue change reaches the topic's audience by the same rule as its stream, and a private topic's queue reaches only its owner's and the sees-all groups (hub seam).
- [ ] Asking for a topic's queue is refused "No such conversation." for one the caller may not see (hub seam).
- [ ] A page catches up on the queue on topic open, on resume and on reconnect (client seam).
- [ ] Other people's chips carry their names and no actions (client seam).
- [ ] E2E: a second page on the same topic shows the same chips as the sending page.
