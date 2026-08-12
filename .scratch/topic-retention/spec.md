# Topic retention: a paginated list and an archive horizon

Status: ready-for-agent

## Problem Statement

The chat client's sidebar shows every topic a space has ever had, all at once. Nothing bounds
how many there are. The voice channel drops its satellite mapping after five idle minutes, so
every gap in speaking mints another topic named after the room, and scheduling mints one per
fire named "Scheduled task". The result is a sidebar of near-identical rows that the person
using it did not create and cannot reasonably prune by hand, since the only way to remove one
is to delete it individually.

Reading that list is expensive in a way that gets worse with every row. The server finds
topics by scanning every key in the store, then the client loads the entire message history of
every topic before it renders anything. Opening the chat client costs one scan, one read per
topic and one full history read per topic.

The only thing keeping this from being worse is a retention rule nobody chose: topics vanish
thirty days after their last write, except that any topic with a browser attached has its
clock reset on every streamed chunk, so it never vanishes at all. Conversations worth keeping
are deleted arbitrarily while the rows nobody wants persist.

## Solution

The sidebar loads a page at a time, most recently written first, and fetches more as the
person scrolls. A topic's history loads when the topic is opened rather than on start-up.

Topics stop appearing in the ordinary list six months after anything last wrote to them. They
are archived, not deleted: they stay whole, they can be found through search, and an explicit
filter lists them. Writing to an archived topic makes it current again, and it reappears in
the ordinary list with no further action.

Twelve months after the last write, a topic is purged, taking its history, what it is searched
by and the files sent to it with it.

Search moves to the server and covers what was said in a topic as well as what it is called,
so a conversation from a year ago can be found by something in it.

## User Stories

1. As someone opening the chat client, I want the sidebar to appear immediately, so that I am
   not waiting on conversations I have no intention of reading.
2. As someone opening the chat client, I want only the first page of topics fetched, so that
   the cost of opening it does not grow every week.
3. As someone scrolling the sidebar, I want the next page to load as I reach the bottom, so
   that reaching an older conversation is continuous rather than a separate action.
4. As someone scrolling the sidebar, I want a topic that gains a message while I scroll to
   move to the top rather than appear twice, so that the list stays trustworthy while I use it.
5. As someone scrolling the sidebar, I want a topic that gains a message below my scroll
   position to still reach me, so that activity is never silently lost to paging.
6. As someone whose connection dropped, I want the list to be true again when it comes back,
   so that I am not looking at a stale sidebar without knowing it.
7. As someone opening a topic, I want its messages to load then, so that opening the client
   does not pay for conversations I never look at.
8. As someone opening a topic, I want the whole conversation available once it is open, so
   that scrolling back through it needs no further waiting.
9. As someone looking at the sidebar, I want each row to show a snippet of what was last said,
   so that I can tell apart the many topics that share a name.
10. As someone looking at the sidebar, I want unread counts on rows I have not opened, so that
    the badge means something for exactly the conversations I have not read.
11. As someone looking at the sidebar, I want the agent-activity indicator to keep working, so
    that I can see an agent is busy without opening anything.
12. As someone who has not touched a conversation for six months, I want it out of my ordinary
    list, so that the list is about what I am currently doing.
13. As someone whose satellite keeps minting topics, I want those topics to age out on the same
    terms as everything else, so that the list is self-limiting without me maintaining it.
14. As someone looking for an old conversation, I want a filter that lists archived topics, so
    that archived means put away rather than lost.
15. As someone browsing archived topics, I want that list paged the same way, so that a long
    archive is no more expensive to open than a short one.
16. As someone who opens an archived topic and replies, I want it back in my ordinary list, so
    that I never have to unarchive anything by hand.
17. As a satellite that reconnects onto an old conversation, I want writing to it to make it
    current, so that a live conversation is never invisible.
18. As someone searching, I want to search by what was said and not only by the topic's name,
    so that I can find a conversation I remember the content of but not the title.
19. As someone searching, I want results from archived topics too, so that search is the way
    into the archive.
20. As someone searching, I want the search to run on the server, so that it covers topics that
    were never loaded into my client.
21. As someone with two tabs open, I want a topic deleted in one to disappear from the other,
    so that I am not looking at a row that no longer exists.
22. As someone whose conversation has not been written to for a year, I want it and everything
    belonging to it removed together, so that no orphaned history or files are left behind.
23. As someone who sent files to a conversation, I want those files to live as long as the
    conversation does, so that an old conversation is not a list of missing attachments.
24. As someone reading a conversation whose files are already gone, I want to see what was sent
    rather than an error, so that the record is still legible.
25. As the person running this, I want the archive and purge horizons configurable in one
    place, so that changing retention is one edit rather than four.
26. As the person running this, I want the horizons to take effect on the next read, so that
    changing one does not require a rebuild, a migration or a backfill.
27. As the person running this, I want storage bounded by the purge horizon, so that a run of
    topic creation cannot grow the store without limit.
28. As the person running this, I want no orphaned index entries left behind by purged topics,
    so that the structure the list is read from does not accumulate dead weight.
29. As a developer, I want one implementation of topic storage rather than two kept in step by
    hand, so that a change to the storage rules cannot be applied to one and forgotten in the
    other.
30. As a developer, I want the list read one way only, so that a new caller cannot reintroduce
    a full scan.
31. As a developer, I want the topic's last-write time stamped wherever a topic is written, so
    that a conversation with no browser attached is ordered and retained on the same terms as
    one with a browser attached.
32. As a developer, I want unread answerable without reading any message, so that showing a
    badge does not cost a history read per row.
33. As a developer, I want existing topics to appear in the new list without being touched
    first, so that upgrading does not silently hide conversations.
34. As a developer, I want the scan-based listing removed rather than kept as a fallback, so
    that there is a point at which it is provably dead.

## Implementation Decisions

### Storage consolidation

`RedisThreadStateStore` in `Infrastructure` and `RedisStateService` in `McpChannelSignalR` are
two hand-synchronised implementations of one key scheme. They collapse into a single
implementation behind `IThreadStateStore`, and `ChatHub` depends on that interface rather than
on a concrete channel-local class. This happens first, so the topic index has exactly one
writer. Consistent with ADR 0001.

A side effect worth using: `ChatHub`'s constructor currently takes the concrete class, which is
why unit-level hub tests pass it null and cannot exercise any topic method. After the
consolidation they can.

### The topic index

One sorted set per agent and space, scored by when each topic was last written to. It becomes
the only way the topic list is read. Its glossary entry is `Topic index` in `CONTEXT.md`.

- Reading the ordinary list is a descending range query from a cursor down to the archive
  cutoff. Paging is keyset paging over the same structure that defines the order.
- Reading the archive is the same query over the range below the cutoff.
- `Archived` is a position in this range and never a field on a topic. Nothing marks a topic
  archived and nothing unmarks it.
- The store takes a `TimeProvider` and subtracts the horizon at query time, so changing the
  horizon takes effect on the next read.

Recorded as ADR 0024.

### Last-write time

The existing last-message timestamp on the topic keeps its name but changes who writes it. It
is currently written only by the browser's streaming handler, so topics driven by voice or by
a schedule never set it. It is stamped server-side wherever a topic's history is appended to,
and it is the score in the topic index. Every retention decision reads this one value.

### Purge

Purge is the store's existing key expiry, raised to twelve months and refreshed on write.
Expiry drops the topic's key but leaves its index member behind, and those members sit below
the archive cutoff where nothing reads, so nothing would ever notice them. The index is
trimmed by a single range-removal at the purge cutoff: score is last-write, so everything below
it is expired by definition and no scan is needed to identify it.

Attachment retention moves off its own thirty-day clock onto the purge horizon, so a topic and
the files sent to it are removed together.

### What the topic record gains

- A message count, so unread is a subtraction rather than a history read.
- A read position replacing the stored last-read message id. Redis lists cannot find a message
  by id without scanning, which is the cost this removes.
- A truncated snippet of the last message, written on the same path that stamps the last-write
  time.

### Hub contract

- The topic-list call takes a cursor and a page size and returns a page plus the cursor for the
  next one, along with which of those topics have a topic stream in flight. This replaces the
  per-topic stream-resume sweep the client runs today.
- A separate call, or a flag on the same one, reads the archived range.
- Search is a hub call over topic names and content, spanning both ranges.
- `DeleteTopic` starts broadcasting the deleted notification to the space. The notification type
  and the client handler already exist; only the send is missing.

### Search

One searchable document per topic holding its text, maintained on append and indexed with
RediSearch, which the stack already runs and the memory store already uses. Search returns
topics, not message positions, because the sidebar's question is which conversation, not which
message. The document carries the same expiry as the topic key and is refreshed on the same
write, so it is purged with its topic.

### Client

- History loads on topic open. The eager per-topic history load on start-up, agent switch and
  reconnect is removed.
- Cursor tracking, page appending and deduplication by topic id move out of the sidebar
  component into a plain logic class the component drives.
- New activity arrives as a push and inserts at the top; paging only ever fetches backwards.
  Deduplication by id covers a topic that gains a message after being paged past. A topic bumped
  from below the cursor to above it is delivered by the push, which is what covers the row the
  cursor will now never reach. A bump that happens while the client is not live is covered by
  catch-up refetching the first page on becoming live.
- Unread counts and row previews read the fields the server now supplies instead of computing
  from loaded history.
- Read state is still persisted server-side on the topic; only its representation changes.

### Migration

Run once from the Agent host on start-up: build the index from the existing keys, backfill
message counts, set every read position to fully read, and raise the expiry. The scan-based
listing is deleted rather than kept as a fallback.

Existing stored read positions are not resolved. Every topic is marked read once, at migration.

The SignalR channel does not wait for the migration. A channel started against an unmigrated
store serves an empty sidebar until it completes.

### Configuration

One retention policy block in the Agent's `appsettings.json` holding the archive horizon (six
months), the purge horizon (twelve months), the page size, the snippet length and attachment
retention. These are generic tunables, so nothing goes in the compose file or its `.env`. The
thirty-day value currently defined in four places, three of them hardcoded, is removed.

### Delivery

One change. The tickets in `issues/` sequence the work on a single branch in dependency order,
each staying green as it lands, merged once at the end. The migration and the features that
depend on it arrive together rather than master sitting with an index nothing reads.

TDD throughout, per the repo's red-green-refactor rule.

## Testing Decisions

A good test here asserts what a caller can observe: what the list returns for a given cursor
and clock, what survives a write, what is gone after purge. It does not assert the shape of a
Redis key, the number of round trips, or that a particular method was called. Cutoffs are
exercised by advancing a fake clock, never by sleeping.

### Seam 1: `IThreadStateStore` against real Redis (primary)

Nearly everything lands here: the index, keyset paging, the archive cutoff as a range boundary,
un-archiving on write, tombstone trimming, server-side stamping of last-write time, message
counts, read positions, snippets, the search document and the migration.

Prior art: `RedisThreadStateStoreTests` and `RedisStateServiceTests`, both `IClassFixture` over
`RedisFixture`, which leases a database per test class from a pooled `redis-stack-server`
container. `RedisThreadStateStoreTests` already covers tail reads and message counts. The second
file folds into the first as the duplicate implementation is deleted.

The store takes a `TimeProvider`; tests use `FakeTimeProvider` and advance it. `ArmedClock` is
not needed unless the tombstone trim ends up on its own background loop. Prior art for a
time-based retention rule is `AttachmentRetentionTests`, which advances a fake clock past the
retention window and asserts what the sweep removed.

Per the repo's testing rule, prefer `RedisFixture` over a mocked store. No in-memory
`IThreadStateStore` exists and none should be created for this.

### Seam 2: `ChatHub`

Only what the store cannot answer: the paged list contract, the archived range, search, the
deleted broadcast, and live-stream reporting alongside the page.

After the consolidation the hub takes the interface, so most of this is unit-level, following
the existing hub unit tests that build the hub with a registered caller context. What needs
real Redis semantics follows `ChatHubDeleteTopicTests`, which is currently the only test
exercising a hub topic method end to end.

### Seam 3: `Tests/Unit/WebChat.Client`

Mostly rewrites of existing tests. The initialization, agent-selection and reconnection effect
tests stop asserting that every topic's history reaches the store. The unread selector tests are
rewritten against server-supplied counts rather than loaded history.

The extracted paging logic class is tested here: cursor advance, appending a page, deduplicating
a pushed topic already held, and a pushed topic that was never paged to.

Prior art: `FakeTopicService` is the ready-made fake, with seeding, recorded calls, per-method
faults and a gate for interleaving a user action with a round trip. `ChatInputLogicTests` is the
precedent for testing extracted component logic as a plain class.

### Seam 4: `Tests/E2E/WebChat`

One test for scroll-to-load-more, on the existing topics collection, alongside
`WebChatTopicManagementE2ETests`. Real-browser scroll behaviour with rows reordering underneath
is the case a fake cannot reproduce; the suite already has a helper for waiting out row
reordering.

### Not tested

No component-render seam is added. `TopicList` stays covered by E2E only, which is why the
paging logic is extracted rather than tested through the component.

## Out of Scope

- **Fixing the mints.** The voice channel keeps rolling over to a new topic after five idle
  minutes with a non-unique name, and scheduling keeps minting one per fire. Both are worth
  fixing and neither is fixed here.
- **An explicit purge job.** Purge stays a consequence of key expiry plus an index trim. Nothing
  reports what was purged and nothing can be purged on demand.
- **Per-message search.** Search finds topics, not positions within them. Jumping to a matching
  message is not supported.
- **Paging a topic's own history.** Opening a topic loads all of it.
- **Origin- or engagement-based retention.** One horizon applies to every topic regardless of
  what created it or whether anyone opened it.
- **Restoring or archiving by hand.** There is no archive verb and no unarchive verb; both
  follow from the clock.
- **Adding bUnit.**
- **Testing the attachment sweeper's background loop**, which is untested today and stays that
  way.
- **Resolving existing read positions during migration.**

## Further Notes

**The archive tier ships inert and stays inert for six months.** Nothing currently stored can be
six months past its last write, because the thirty-day expiry already removed anything that got
close. Pagination and lazy history are the only parts that change anything at launch. The
archived filter will be empty until six months after the expiry is raised.

**The list gets longer before it gets shorter.** Today the expiry caps the sidebar at thirty
days of voice rollovers; afterwards it holds six months of them, roughly six times the rows and
twelve times the stored bytes. Pagination is what absorbs that, which makes it the load-bearing
part of this work rather than a nicety.

**A reader will not find the archiving code.** There is no flag, no job and no archive verb,
only a cutoff subtracted from the current time when a range is built. The absence is the design
and is easy to mistake for an omission. ADR 0024 exists so that is recoverable.

**The migration is the piece with no rollback**, and it lands in the same change as everything
else, over a storage layer whose consolidation has no tests on it today. The consolidation is
therefore done first and separately within the change, keeping the existing store tests green
before anything new is added.

**Glossary.** `CONTEXT.md` gained `Topic`, `Topic index`, `Archived` and `Purge`. Use them.
`Topic` is the persisted record; `Conversation group` remains the runtime counterpart and is
never persisted.
