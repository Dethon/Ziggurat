# 06: Stop returns queued messages to their senders

**What to build:** Stop ends the running reply as today and empties the queue. Every queued message goes back into its own sender's composer on that topic, merged the way Edit merges.
- **Shared topics.** This holds for every sender, so a member whose message was queued when another member pressed Stop gets theirs back rather than having it run or vanish.
- **One device per person.** A person's returned messages land on exactly one of their devices: the first page that takes them.
- **No page open.** A sender with no page on the topic finds them in the composer the next time they open it.
- **Nothing runs** after the stop.

**Blocked by:** 05.

**Status:** done

- [x] After a stop the queue is empty, each sender's items are held for them in order, and no further turn is emitted (hub seam).
- [x] Each sender is told their items are waiting. A take hands them over once, and a second take, from any of their devices, gets nothing (hub seam).
- [x] A page takes returned items when told while showing the topic, and on every topic open, and merges them into the composer (client seam).
- [x] An installed page from before, which ignores what Stop returns, still stops the reply (hub seam).
- [x] E2E: two members on a shared topic each queue a message, one presses Stop, and each finds their own text in their composer with no further turn run.

**Notes from the implementation:**
- Hub: `TakeReturned(topicId)` → `IReadOnlyList<ReturnedMessage>` (empty when none); push `OnQueueReturned` with one record, `QueueReturnedNotification(TopicId, SpaceSlug)`, to `account:{userId}`. Both of the stop's pushes are best-effort, so a stop never fails on them and its approvals are still cancelled.
- The returned set lives in `StreamService` (`_returned`, keyed by topic and sender account, under the stream lock), outlives the stream, and is gone once taken, when the topic is deleted (`StreamService.Forget`, the user's delete; a delete of a streaming topic from the page stops it first, so its queue is returned and then dropped with the topic) or when the hub restarts. A page showing the topic may take them between the stop and the delete, and its composer then keeps the words after the topic is gone.
- Dispatched-not-started already existed implicitly: a head taken as the turn ended, before the dispatcher wrote it on the stream, was lost to a stop. It is now held per topic by id (`StreamService.Start`, which the dispatcher must win before writing) and returned first. Ticket 07's steers can go there.
- A Remove or Edit that loses to a stop answers "already sent", although the message was returned to the composer.
- Residual, unchanged: a stop after the dispatcher wrote the head on the stream but before it was emitted leaves it drawn on the stream, neither sent nor returned; and the cancel is emitted to the agent before the queue is returned, so a turn ending in between can dispatch the next head into the cancel.
- Ticket 03's double completion, not fixed here: a turn the agent ends with an `Error` and then a `StreamComplete` completes the stream twice, so two queued heads are dispatched at once (`ATurnEndedByAnErrorThenAStreamEnd_StillRunsEachQueuedMessage` pins only that neither is lost); with one message queued the second completion looks to end the stream under it.
- A message still being admitted when the stop lands is neither queued nor returned: `EnqueueMessage` answers false and the page sends it as a new turn right behind the cancel.
- The page takes on `OnQueueReturned` while showing the topic, on every topic open and on becoming live again (`ReturnedMessagesEffect`); an answer landing after the person moved to another topic is kept on the page for that topic's next open.

