# Ziggurat

An AI agent reachable over several transports, with its own tools, memory and
observability. This file is the glossary: what each term means here, and which
near-synonyms not to use for it. It holds no implementation detail.

## Observability

**Metrics publisher**:
The fire-and-forget thing a caller holds to record a metric. Publishing through it
cannot fail, cannot block and cannot be observed.
_Avoid_: metrics client, metrics writer, telemetry publisher

**Metric sink**:
The transport a metrics publisher drains into. Sending through a sink is a real
network operation that may fail.
_Avoid_: metrics backend, metrics transport, metrics exporter

## Metrics dashboard

**Metric family**:
One kind of metric as the dashboard shows it: the events it lists, the breakdown it
charts, and the choices the user has made about how to see them. Each family gets its
own page, and a family is the unit anything about the dashboard is added or changed in.
_Avoid_: metric type, metric category, page

**Breakdown**:
A metric family's numbers grouped by one dimension over one date range. It is what a
family's chart draws, and a family only ever has one at a time — choosing differently
replaces it.
_Avoid_: grouping, chart data, aggregation

**Dimension**:
What a breakdown groups by: who sent it, which model, which stage, which satellite. It
says how to split the numbers up and never what the numbers are.
_Avoid_: group by, facet, axis

**Aggregation**:
How a set of durations is reduced to one number — the average, a percentile, the
largest, or how many there were. It is a separate choice from which quantity is being
measured, and where both apply they are chosen independently.
_Avoid_: metric, statistic, rollup

## Client live connection

These terms apply to both browser clients, the chat client and the metrics
dashboard. They share the vocabulary and not the code: each client has its own
implementation, and `docs/adr/0008-the-two-browser-clients-stay-separate.md`
records why. Terms marked _chat client only_ describe something the dashboard
does not have.

**Live connection**:
The client's single ongoing link to its hub. It outlives any one transport
instance: it survives being torn down and built again, and while it is live the
client receives server pushes.
_Avoid_: connection service, hub client, socket

**Hub connection**:
One transport instance underneath a live connection. It is disposable and gets
replaced whole; nothing outside the live connection holds one across a replacement.
_Avoid_: connection, channel, socket

**Rebuild**:
Throwing away the hub connection and building a new one. Everything bound to the
old instance is gone afterwards.
_Avoid_: reconnect, restart, refresh

**Reconnect**:
The transport restoring itself without being replaced. The same hub connection
comes back, so anything bound to it is still bound.
_Avoid_: rebuild, retry

**Becoming live**:
The moment the client can talk to the server again, whether it got there by a
rebuild or a reconnect. Callers that must recover after an interruption care about
this, never about which of the two happened.
_Avoid_: connected event, online

**Connection epoch**:
How many times the client has become live. Two epochs being different is the only
reliable way to tell that an interruption happened, because a recovery can start
and finish without anyone observing a disconnected state in between.
_Avoid_: generation, connection id, sequence number

**Catch-up**:
Re-reading whatever arrived while the client was not listening, so that what is on
screen is true again. It is needed because a hub only pushes what happens while
someone is attached and never replays a gap. It never runs on the first
connection, where ordinary start-up loads the same data.
_Avoid_: reload, refresh, resync, backfill

**Session recovery** (chat client only):
The work that has to happen every time the client becomes live again for the
server to treat it as the same user in the same space: identifying the user,
rejoining the space, re-sending the push subscription. It never runs on the first
connection, where ordinary start-up does that work with the extra steps the first
connection needs. It re-establishes an identity and is a separate job from
catch-up, which re-reads data.
_Avoid_: reconnection handler, resubscribe, rehydrate

**Hub call** (chat client only):
Something the client asks the server to do over the live connection and waits on:
fetching topics, sending a message, answering an approval. It goes in the opposite
direction to a server push, and unlike a push it can only happen while the
connection is live.
_Avoid_: invoke, request, RPC, server call

**Not live** (chat client only):
The answer a hub call gives when it could not be made, because the client was
between connections or still getting one up. It is one of the two things any hub
call can come back with, the other being the server's own answer, and it never
means the server said no.
_Avoid_: disconnected, offline, failed, null

## Conversation

**Turn**:
One message the agent answers, and everything that comes back from it. It is what
waits its place in a conversation, what decides where its replies go, and what
reports how long the answer took. A conversation runs one at a time.
_Avoid_: message, request, exchange, prompt

**Turn key**:
What a turn is dispatched under, so a reply can say which turn it answers. Every
reply of a turn carries it back, and the receiving side compares rather than infers:
a conversation outlives a turn, so asking the conversation gives the wrong answer
whenever an answer is late, an announcement arrives mid-conversation, or a device
reconnects. Every turn has one, whether the channel it came from supplied it or not.
_Avoid_: message id, correlation id, stream id, request id

**Chat command**:
A message that steers the conversation instead of being answered by it. It never
reaches the agent and produces no reply, so it is not a turn. It also never waits
behind one: stopping a turn is itself a command, and it is useless unless it arrives
while the turn it stops is still running.
_Avoid_: control message, instruction, slash command

**Conversation group**:
Every message for one conversation and one agent, taken as a single thing. The
agent, the history it restored and the conversation its replies land in belong to
the group rather than to any one turn, and none of it exists until the group's first
turn does — a group that only ever carries commands builds nothing.
_Avoid_: session, thread, conversation

**Delivery identity**:
The conversation a turn's replies actually land in, which is not always the one its
message came from. Everything the turn produces is filed under it: the history it
appends to, the approvals it raises, the events it reports. The rule is to name a
conversation someone can open, so a scheduled task fires under the conversation it
delivered into and not under the schedule.
_Avoid_: conversation id, target conversation, delivery key

## Chat streaming

**Topic stream** (chat client only):
A topic's one reply in flight, from the send or resume that opened it to its single
ending. A topic has at most one, and it ends exactly once however it ends: the reply
finishing, the stop button, or the topic being deleted.
_Avoid_: active stream, streaming state, stream session

**Stream lease** (chat client only):
What the opener of a topic stream holds. It is the only way to add to that stream or
end it, and a stale one can do neither — once the topic has moved on to another
reply, the lease that opened the old one no longer speaks for it.
_Avoid_: stream handle, stream token, stream id

## Chat attachments

**Attachment**:
A file a person sends as part of a turn. It is part of what they said rather than
something added on their behalf, so it is never decoration, and it reaches the model
as content of the same message as the text. A turn may carry attachments and no text
at all.
_Avoid_: upload, file, media, blob

**Attachment reference**:
What stands in for an attachment everywhere the bytes are not wanted: where the file
rests, what kind it is, what it is called and how large it is. It is what a
conversation's history keeps, so reading a history costs the same whether or not
files were ever sent.
_Avoid_: file id, attachment metadata, pointer, handle

**Upload store**:
Where an attachment's bytes rest between being sent and being read, for a channel that
would otherwise have nowhere to keep them. It is a holding area and not a place anyone
works: nothing is edited there, nothing is found there by looking, its contents are
reached only by naming a reference, and what it holds is eventually swept. A channel
whose transport already keeps the file has none.
_Avoid_: attachment store, staging mount, uploads folder

**Hydration**:
Putting an attachment's bytes back where its reference sits, on the way to the model
and never on the way in. It reaches back a fixed distance, so an attachment stops
being visible to the model long before its reference leaves the history. A reference
whose bytes are gone hydrates to a placeholder naming the file.
_Avoid_: rehydration, inlining, resolving, expansion

**Attachment capability**:
Whether a model accepts a kind of attachment at all. It belongs to the model and not
to the agent or the conversation, so it changes when the model does, and it is always
asked of the model the turn will actually run on.
_Avoid_: modality support, model features, vision support

## Dictation

**Dictation**:
One run of a microphone that ends as words. It is never a message and never becomes
one: the audio lives only until the words are out of it, nothing keeps it afterwards,
and what is finally sent cannot be told apart from typing. A person holding a button
in the chat client and a person recording in Telegram are both dictating. It may also
end with nothing said, the audio thrown away before any words are asked of it, which
is not a failure and leaves no trace at all.
_Avoid_: voice message, audio message, recording, voice note

**Transcript**:
The words a stretch of audio turns out to hold, and the only part of it anything
downstream ever sees. It carries no trace of the audio — not how long it was, how loud
it was, or that there was audio at all.
_Avoid_: transcription, recognition result, speech text

**Latched dictation** (chat client only):
A dictation carrying on with nothing held down. It is the same dictation and not a new
one: the same audio keeps accumulating under the same clock, and only the way it can end
changes, from letting go to pressing.
_Avoid_: locked recording, hands-free, toggle mode

## Channel connection

**Connection generation**:
One unbroken run of the agent's link to a channel, from the moment it is established
to the moment it drops. Anything the agent learned about the channel by asking it
belongs to that run and no other, because the next run may be talking to a different
process.
_Avoid_: session, connection instance, epoch

**Not connected**:
The state a channel link is in before its first connection and for the whole of a
reconnect. It is not one behaviour: each thing a caller can ask for answers it in the
way its own caller needs, and which way that is belongs to the question rather than to
the state.
_Avoid_: disconnected, offline, dead

## Memory

**Decoration**:
Everything prepended to a user turn on its way to the model that the user did not
type: who sent it, from where, when, what alert they dismissed, and what the agent
remembers about them. It exists only on the copy sent to the model and is never
persisted.
_Avoid_: prefix, envelope, wrapper

**Recall block**:
The decoration that carries remembered facts. The model is told to look for it by
name.
_Avoid_: memory context, memory prefix

**Extraction window**:
The slice of conversation the memory extractor reads, rendered with turn markers so
the extractor knows which turn is the current one.
_Avoid_: context window, history slice

**Memory anchor**:
The point in a conversation's persisted history that an extraction window is cut at.
It is taken before the current turn is persisted, so it excludes the turn that
produced it.
_Avoid_: anchor index, offset, cursor

**Unremembered user**:
A user with no stored memory entries. They may still have a personality profile, and
their turns are still considered for extraction, so they can stop being one at any
turn.
_Avoid_: user with no memories, new user, memoryless user, "skip recall" user

## Voice satellite

**Satellite connection**:
One run of the hub's link to a satellite, from the moment it dials to the moment
it has finished unwinding. It is the thing that runs: nothing outside it holds a
reference to it, and a drop ends it for good — the next attempt is a new one.
_Avoid_: satellite session, link, socket

**Satellite session**:
The satellite as something the rest of the hub can address. Callers that have
nothing to do with the wire — a reply being spoken, an announcement, an alarm —
find it by satellite id and use it to queue audio, send a control event or read
the current turn. It lives exactly as long as one satellite connection, but it is
reached from the outside rather than run.
_Avoid_: satellite connection, satellite state

**Wake announcement**:
What the satellite tells the hub when its own wake detection fires: how loud the
wake word was, how confident it was, what triggered it, and how loud the room was
just before. Every part of it is optional, because it comes from a peer with no
schema and older firmware sends none of it.
_Avoid_: wake signal, wake event, wake metadata

**Wake turn**:
The turn a wake announcement opens. It is the only turn whose loudness is worth
recording, because it is the only one the user announced by speaking the wake word.
_Avoid_: first turn, initial capture

**Follow-up turn**:
A turn the hub opens by itself after a reply, with the microphone live and no wake
word. It has no wake announcement of its own and never will.
_Avoid_: continuation, second turn

**Capture**:
The satellite's microphone while it is open, from the moment the hub starts listening
to the moment it stops and says what the room sounded like. It knows nothing about
turns: a wake turn and a follow-up turn each open one, and so does a question the
agent asks mid-turn, which is not a turn at all.
_Avoid_: recording, mic session, utterance

**Satellite identity**:
Which satellite something is about, said once: the satellite itself, the room it is
in, and the person it belongs to. Everything the hub reports about a satellite names
it this way, so a report cannot name two of the three and forget the last.
_Avoid_: satellite id, room, device info

**Reply speaker**:
The one thing that turns an agent's answer into audio on a satellite — deciding what
is a speakable segment, having it synthesised, queueing it, and reporting how it went.
It serves both a live answer and one delivered to a satellite that was not listening
when it was written.
_Avoid_: reply handler, send reply, TTS pipeline

## Satellite playback

**Playback queue**:
The one place audio waits its turn to be heard on a satellite. Only one thing is
audible at a time, so everything anyone wants spoken there — a reply, an
announcement, an alarm, a prompt, an earcon — goes through it and is heard in the
order it accepted them.
_Avoid_: playback loop, audio channel, speaker

**Playback job**:
One stretch of audio handed to the queue to be heard as a unit, along with what kind
of thing it is. The kind is what the queue reads to decide how much of that kind it
will hold at once and how it is treated on the way out.
_Avoid_: playback item, utterance, segment

**Playback outcome**:
The single fact about a job that ends it. Every job accepted or turned away gets
exactly one, and never more: it was heard to the end, it was cut short, it broke, it
was turned away, or the connection died before it could be heard. Whoever queued the
job learns it from that one fact and nothing else.
_Avoid_: playback result, callback, completion

**Refusal**:
An operation declined outright with a stated reason. A refusal is not a failure: the
caller is told why, and nothing was attempted. Two places use the word. The playback
queue refuses a job so it never waits and is never heard, for three reasons — the
satellite is gone, the queue already holds as much of that kind as it will, or a
low-priority job arrived while anything was queued. The media library refuses a file
operation that a live download owns, for five reasons — reading text that is not a
status file, reading a rendered status file as bytes, landing anything inside a live
download's directory, moving any of that directory out, and deleting a path that is
neither a download nor its status file.
_Avoid_: rejection, drop, error

**Preemption**:
A high-priority job cutting the line. Everything already queued when it arrived is
cut short as well, not just whatever was audible at that moment, so an alarm is heard
next rather than after the rest of an answer.
_Avoid_: cancellation, interruption, barge-in

**Drain**:
A job being heard all the way to its end. It means the satellite finished playing,
not that the hub finished sending — the hub is always done sending first, because the
satellite plays at real time.
_Avoid_: completion, finish, flush

## MCP server hosting

**MCP host**:
The three things every MCP server in the repo has, whatever else it does: its own
settings available to everything it registers, a server, and an HTTP transport.
_Avoid_: server host, web host, bootstrap

**Tool server**:
An MCP server that offers the agent things to call — tools, prompts, filesystem
mounts — and nothing the agent can be pushed from. It only ever answers.
_Avoid_: mcp server, backend server, resource server

**Channel server**:
An MCP server the agent can be pushed from. A person or an event reaches the agent
through it, and a reply comes back the same way.
_Avoid_: transport server, gateway

**Dual-role server**:
A server that is both at once: it offers the agent tools and it can also raise
something with the agent unprompted. Its channel-protocol tools are hidden from the
model.
_Avoid_: hybrid server, mixed server

**Outbound surface**:
A channel server's ability to carry a reply back to a person. Direction is named from
the agent's side, so a reply travelling agent → channel → person is outbound. A
dual-role server that only ever raises alerts has no outbound surface: it accepts the
reply and drops it.
_Avoid_: inbound surface, reply channel, delivery path

**Mount identity**:
The one name a filesystem mount is known by. Its resource address, its mount point
and the name it publishes to the agent all come from it, so they cannot disagree.
_Avoid_: mount name, filesystem name, mount point

**Move-out check**:
The question a mount answers before a path leaves it. A move between two mounts is a
copy followed by a delete of the source, so the mount losing the path is never asked
to move anything and its own refusals never run. The check asks it first, before any
byte is streamed, and a mount with nothing to say allows it.
_Avoid_: move guard, cross-mount guard, pre-move check

## Virtual filesystem

**Virtual path**:
A mount point followed by a path under it. It is the only coordinate system that crosses
the tool boundary: the only spelling the filesystem prompt teaches, the only one the
registry resolves, and the only one that may appear in a tool's answer. Anything the model
reads out of a response can be passed straight back into another tool.
_Avoid_: full path, absolute path, mount-relative path

**Backend coordinates**:
How a backend spells a path to itself — container-absolute for a disk root, mount-relative
with or without a leading slash elsewhere. Backends disagree with each other, and that is
fine on the wire. It must never appear in a response the model sees.
_Avoid_: real path, physical path, native path

**Transfer**:
A copy or a move across any two virtual paths — same mount or not, file or directory. It is
one operation with one answer, and it is not a backend's native copy or move: those work
inside one mount, while a transfer may span two, recurse a directory and report what
happened to each entry.
_Avoid_: copy, move, cross-mount copy, file operation

## Media library

**Live download**:
A download the download manager still knows about. While it is live it owns its
directory and everything in it, which is what every refusal on the media library is
asking about. When it ends, it stops owning anything: what is left is ordinary files.
_Avoid_: active torrent, running download, download task

**Status file**:
The live state of one download, shown as a file. It is a view rendered when asked, so
it can be read and nothing else — copying or writing it would produce a stale snapshot
under a name that still looks live.
_Avoid_: status.json, virtual file, progress file

**Leftover**:
A file or directory left where a download used to be, after the download itself is
gone. It is an ordinary file: it can be listed, read and removed like any other, and
it is never mistaken for a status file.
_Avoid_: orphan, stale download, ghost

## Agents

**Agent definition**:
The configured description of an agent. It is long-lived and shared, and says
nothing about any one conversation.
_Avoid_: agent config, agent profile

**Agent spec**:
Everything needed to build one running agent, resolved from a definition at the
moment it is built. It carries the conversation it belongs to and the identities it
will report under, so no two running agents share one.
_Avoid_: agent options, agent config, agent definition

**Subagent**:
An agent that another agent spawns for one task. It keeps no history, and it runs
under the parent's conversation because it acts on the parent's behalf.
_Avoid_: child agent, worker, nested agent
