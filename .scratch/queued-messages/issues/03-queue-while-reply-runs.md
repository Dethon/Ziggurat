# 03: Queue a message while a reply runs

**What to build:** The tracer bullet.
- **Sending mid-reply.** While a reply is streaming in WebChat, the composer still sends. Stop sits beside Send, and Enter queues.
- **The hub holds the message.** The chat hub keeps it in the topic's queue, in memory beside the topic stream, instead of sending it to the agent. The person sees it as a chip above the composer: an excerpt, and a file count when it has files.
- **Dispatch at turn end.** When the running turn completes, the hub dispatches the head of the queue from its sender snapshot. It writes the person's message on the stream, vouches for it afresh, and emits it. The stream stays open until nothing is pending or queued.
- **Drawing.** The message appears in the conversation only when its turn starts. The reply that was streaming is never cut into two bubbles by a queued send.
- **Racing a stream.** A `SendMessage` that arrives on a topic already streaming is queued too, not sent ahead of the queue.

**Blocked by:** 01.

**Status:** done

- [x] Two messages queued behind a running reply run one after the other, each written on the stream only when its turn starts (hub seam).
- [x] The stream stays open across the hand-offs and ends after the last queued turn (hub seam).
- [x] A session signed out while its message waits is refused "signed out" at dispatch, and the next queued message still runs (hub seam).
- [x] A queued message runs after its sender's connection has closed (hub seam).
- [x] A `SendMessage` racing a running stream is queued, not emitted ahead of the queue (hub seam).
- [x] Concurrent enqueues from two connections keep their order under two concurrent test processes (hub seam).
- [x] The composer sends while streaming and shows Stop beside Send. A queued send draws nothing and leaves the reply bubble open, and the dispatched message is drawn once, after the reply it followed (client seam).
- [x] Chips show the queue in order and disappear as messages are dispatched (client seam).
- [x] E2E: behind a tool-using reply, queue two messages, see two chips, and see both run in order.

**Notes from the implementation:**
- Stop (`CancelTopic`) empties the queue for now, so nothing queued is sent into a stopped stream. Ticket 06 replaces this by returning the items to their senders.
- `OnQueueChanged` carries one record, `QueueChangedNotification(TopicId, Items, Revision, SpaceSlug)`, as every hub push here does, not two arguments. `Revision` orders pushes from the hub and the dispatcher that may cross. `GetQueue(topicId)` exists; using it in catch-up is ticket 04.
- A message refused as it is queued (`EnqueueMessage`: the capability guard or the session check) is thrown to its sender as a `HubException` and shown as a toast, because ending a turn on the stream would end the one running.
