# 0014 — A live download owns the path, not its spelling

Status: accepted
Date: 2026-08-06

## Context

The media mount is a disk root with the downloads overlay layered on the same directory.
Some paths under `downloads/` mean something to the overlay: `downloads/<id>` is a
download the agent can cancel by deleting, and `downloads/<id>/status.json` is a rendered
view of live state that is not on disk at all. Everything else, including the payload
files the download is writing, is a plain disk entry.

Every operation on that mount therefore has to answer the same question first: does this
path belong to a download right now? The question was answered seven times, by three
different predicates, in two different error shapes.

The three predicates ask different things. `IsVirtualPath` asks how the path is spelled.
`IsLiveVirtualPathAsync` asks whether a download still owns the id. `TouchesActiveDownloadAsync`
asks whether the path and a live download's directory overlap. Crossed with the two shapes
— the tool-error envelope for most operations, a thrown exception for the two chunk
methods, which have no envelope in their signature — that gave fourteen possible answers
to one question, and each of seven call sites picked its own.

They disagreed. `ReadBlobAsync` served a leftover `downloads/<id>/status.json` whose
download no longer exists, because a real file left at that path is the disk's file
(`ead0b827`). `ReadChunksAsync` threw on the same path, because it asked about spelling.
Which one ran depended on whether the caller was doing a ranged read or a streamed one —
that is, on which side of the MCP seam it sat, not on anything about the file. Seven
commits (`2a655f4e`, `ffa0cccb`, `f8cb3cbc`, `7f14527f`, `ead0b827`, `2b57322d`,
`ab637436`) each fixed one more site without removing the shape that produced them.

## Decision

**Liveness decides ownership, and one rule states it.**

The overlay owns a path only while a download owns the id. A `downloads/<id>/status.json`
with no live download behind it is a plain disk file: readable, writable and removable by
the disk, exactly like any other leftover. Spelling alone never makes a path the
overlay's.

The overlay answers one question for the whole mount:

```
Task<ToolErrorResult?> RefuseAsync(DownloadsIntent intent, string path, CancellationToken ct)
```

One path per call, five intents, one message each:

- **`TextRead`** — refuses every path that is not a `downloads/<id>/status.json`, because
  the rest of this mount is bytes.
- **`ByteRead`** — refuses a status.json a live download owns, which is a rendered view
  rather than a file.
- **`Land`** — refuses anything landing inside a live download's directory, because it is
  removed when the download is cancelled. Copy destinations, blob writes, streamed writes
  and the destination end of a move all carry this one reason.
- **`MoveOut`** — refuses moving a live download's directory, an ancestor of it, or
  anything inside it, because the download keeps writing and recreates what it lost while
  the moved copy is orphaned. ADR-0023 narrows this one intent to downloads that have not
  finished: a seeding torrent stays listed, and refusing on that blocked the organising
  move the completion alert asks for.
- **`Delete`** — refuses a live status.json and any path that is neither a download
  directory nor a status.json.

The rule answers "may I" and never performs an effect. Cancelling a download, clearing its
routing entry and recovering a leftover directory stay in `TryDeleteAsync`, which runs
after the rule has said nothing.

Every operation becomes `await RefuseAsync(...) ?? await base.X(...)`. The two chunk
methods call the same rule and wrap its envelope in `FileSystemOperationException`;
`NotSupportedException` goes back to meaning only what `FileSystemBackendBase` uses it
for, an operation the backend does not implement. `GlobAsync` and `InfoAsync` never
refuse and stay out of the rule.

The three predicates become private. No caller can ask half the question again.

## Considered options

**Spelling decides ownership everywhere.** The simpler predicate: any
`downloads/<id>/status.json` is the overlay's, live or not. Rejected because it undoes
`ead0b827`. Info and delete already treat a leftover as the disk's file, so a
spelling-based read leaves it listed by `fs_glob`, reported as existing by `fs_info`,
removable by `fs_delete` and unreadable by everything — the most stuck a file can be. It
also cannot be right in principle: the overlay renders live state, and there is no live
state to render.

**Keep the predicates and add a shared helper per shape.** Fixes the immediate divergence
without changing the surface. Rejected because the surface is the defect. Three public
predicates crossed with two shapes is what let seven sites drift, and a helper that
callers may or may not use does not remove a single one of the fourteen combinations.

**Give the chunk methods an envelope.** Then there is one shape and no wrapping.
Rejected: `ReadChunksAsync` returns `IAsyncEnumerable<ReadOnlyMemory<byte>>` and
`WriteChunksAsync` returns a byte count, both by the streaming contract every backend
implements. Changing two signatures across every backend to remove one wrapper at one
mount is the wrong trade.

## Consequences

- The chunk/blob divergence is unrepresentable. Both halves of a read ask the same rule
  and get the same answer, so a leftover status.json behaves identically whichever side of
  the MCP seam the caller sits on.
- A write to a live status.json is refused as a `Land`, not as a write to a virtual file.
  Both are correct and the reason the agent is told — that the file dies when the download
  is cancelled — is the more useful one.
- Download ids are link hash codes, so re-adding the same link resurrects the same id. A
  file written where a leftover status.json used to be is then shadowed by the revived
  download's rendered view until that download ends. This is the existing behaviour of any
  file inside a download directory, and deleting the directory clears it.
- Tests assert one interface. `DownloadsOverlayTests` carries a matrix over intent by path
  class — live directory, live status.json, payload inside a live directory, leftover
  status.json, unrelated path — and the mount's tests shrink to asserting that each
  operation consults the rule with the right intent.
- The cross-mount move guard is untouched by this decision. `RefuseMoveAsync` delegates to
  the same rule, but the fact that `ICrossMountMoveGuard`'s type test never succeeds
  against the production topology is a separate problem with its own trade-off. ADR-0015
  settles it: the question crosses the seam as `fs_move_out_check`, the interface is
  deleted, and the `MoveOut` intent defined here is what the media mount answers with.
