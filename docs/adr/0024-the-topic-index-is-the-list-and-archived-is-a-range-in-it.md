# 0024 — The topic index is the list, and archived is a range in it

Status: accepted
Date: 2026-08-12

## Context

The chat client's sidebar was read by globbing `KEYS topic:{agentId}:*`, fetching every
matching key, deserializing all of them, filtering by space in memory and sorting in memory.
The cost was O(every key in the database) per call, and the call ran on start-up, on agent
switch and on reconnect. Alongside it the client loaded the full message history of every
topic in parallel on the same three paths, so N topics cost one scan, N gets and N full
history reads before anything rendered.

Nothing bounded N. The voice channel drops its satellite mapping after five idle minutes and
the next utterance mints a fresh conversation, so a room accumulates one permanent topic per
five-minute gap in speaking, all named alike. Scheduling mints one per fire, all named
"Scheduled task". The only thing holding the total down was a 30-day Redis TTL that a browser
resets on every streamed chunk, which is a retention rule nobody chose and which deletes
conversations people would have kept.

Those mints are staying as they are. This decision is about surviving them.

## Decision

**One sorted set per agent and space, scored by when each topic was last written to, is the
only way the topic list is read. Archived is a range within it and not a state a topic
carries.**

Reading the list is a descending range query from a cursor, so pagination is keyset paging
over the same structure that already defines the order. Archived means a score below the
archive horizon: no flag is written, no sweeper runs, and a write to an archived topic moves
its score and makes it current again in the same act. Changing the horizon takes effect on the
next read.

Purge is delegated to the Redis TTL on the topic key, set beyond the archive horizon. Expiry
leaves the index member behind, and those tombstones sit below the archive horizon where
nothing reads, so the index is trimmed by a single `ZREMRANGEBYSCORE` at the purge cutoff.
Score is last-written, so everything below that cutoff is expired by definition and no scan
is needed to find out.

Two consequences follow for the client and are part of the decision rather than fallout from
it. History loads when a topic is opened rather than for every topic up front, so anything the
client used to compute from all loaded history is answered by the server instead: unread count
becomes a subtraction of two positions carried on the topic, and the row preview becomes a
snippet stored on it. Whether a topic has a reply in flight is reported with the page rather
than asked per topic.

The two hand-synchronised Redis implementations, `RedisThreadStateStore` in `Infrastructure`
and `RedisStateService` in `McpChannelSignalR`, collapse into one behind `IThreadStateStore`
first, so the index has exactly one writer.

## Considered options

**Materialize an `IsArchived` flag and run a sweeper.** Explicit and inspectable, and it is
what the word "archive" usually implies. Rejected because the flag and the timestamp are the
same fact in two places, and every window between the write and the sweep is a window where
they disagree. It also makes un-archiving something a caller must remember to do on every
append, which is exactly the class of guard `0017` records the cost of.

**Paginate the response and keep the `KEYS` glob.** No new structure. Rejected because it
helps only the browser: the server still scans every key in the database to build a page, and
the archive rule would then need its own scan on a timer.

**Move to a store that indexes for us.** A relational table with an index on last-written
answers all of this without a hand-maintained structure. Rejected because the repo has no SQL
anywhere, and adding a second database to fix a sidebar is a larger commitment than the problem
justifies.

**Fix the mints instead.** Persist the voice channel's satellite mapping and key scheduled
fires by schedule rather than by fire, so the volume never appears. Rejected as the answer on
its own, because it leaves the list unbounded for the next thing that mints per session and
does nothing about topics already stored. It remains worth doing separately.

## Consequences

- The archive tier does nothing for six months. Nothing currently stored can be six months past
  its last write, because the 30-day TTL already deleted anything that got close. Pagination
  and lazy history are the only parts that change anything at launch.
- The list gets longer before it gets shorter. Today the TTL caps the sidebar at 30 days of
  voice rollovers; afterwards it holds six months of them, roughly six times the rows and
  twelve times the stored bytes. Pagination is what absorbs that, which makes it the
  load-bearing part of this design rather than a nicety.
- A reader looking for the archiving code will not find any. There is no flag, no job and no
  archive verb — only a cutoff subtracted from the current time when a range is built. The
  absence is the design and is easy to mistake for an omission.
- Read state changes meaning. A last-read message id becomes a position, and the existing ids
  are not resolved during migration: every topic is marked fully read once, at migration.
- The migration runs from the Agent host and the SignalR channel does not wait for it. A channel
  started against an unmigrated Redis serves an empty sidebar until the migration finishes,
  rather than falling back to the scan the migration exists to delete.
- Attachment retention moves from its own 30-day clock onto the purge horizon, so a topic and
  the files sent to it now die together at twelve months instead of the files going eleven
  months early.
