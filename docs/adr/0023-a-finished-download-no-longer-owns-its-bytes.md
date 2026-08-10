# 0023 — A finished download no longer owns its bytes

Status: accepted
Date: 2026-08-10

## Context

ADR-0014 settled that a path belongs to the downloads overlay only while a download owns
the id, and that one rule — `RefuseAsync(intent, path)` — answers for every operation on
the media mount. Two of its five intents, `Land` and `MoveOut`, ask the same predicate:
does this path overlap a live download's directory? Live there means one thing, and it is
the only thing the overlay ever asked: the download client still lists the id.

qBittorrent keeps a torrent listed after it finishes. `progress >= 1.0`, `uploading`,
`stalledUP`, `queuedUP` and the stopped-up states all map to `DownloadState.Completed`,
and the torrent stays in the client, seeding, until something removes it. The only thing
that removes it is `fs_delete` on `downloads/<id>`, which is the cancel: it cleans up the
download's files as well.

So the mount refused the one move it exists to permit. `DownloadCompletionPlanner` tells
the agent, in the completion alert itself, to "carry out any follow-up steps you promised
for it (e.g. organizing it into the library)". The agent then moves
`downloads/<id>/payload.mkv` to `Movies/…` on the same mount, and `MoveAsync` asks
`MoveOut` about the source, which overlaps a listed download, and refuses. It would refuse
for as long as the torrent seeds. The only way past it was to delete `downloads/<id>` — to
cancel the download and destroy the file being organised — and the mount's own prose
already promised the opposite: "Once a download ends, what it left behind is ordinary
files."

The refusal's stated reason names the state it is actually about: "the download keeps
writing and recreates what it lost". A finished download writes nothing.

## Decision

**Owning the id and owning the bytes are two different things, and the move asks the
second.**

The overlay keeps one overlap predicate and asks it about two different sets of downloads:

- **`Land` asks about every listed download.** Delete-as-cancel removes `downloads/<id>`
  whole for as long as the client owns the id, so anything placed inside one still dies
  with it, finished or not. The way in is unchanged.
- **`MoveOut` asks only about downloads that are still writing** — `State is not
  Completed`. A finished download's payload files are ordinary files, and they move like
  ordinary files, on this mount or off it.

Only `Completed` frees the files. A paused download resumes into them and a failed one
left a partial the agent should cancel rather than file away, so both keep refusing.

The rendered status file is not covered by that opening. `MoveOut` refuses a status file
any listed download owns, with its own reason: it is rendered on read, so a move would
find no bytes on disk and answer `not_found` for a path `fs_info`, `fs_glob` and `fs_read`
all report as existing — the ghost ADR-0014 exists to prevent. That branch is asked after
the boundary one, so a download still running is told the more useful thing.

## Considered options

**Leave the rule alone and organise with copy plus delete.** No code changes. Rejected
because it does not work: `fs_delete` on this mount removes only download directories and
leftovers, so the source copy can never be removed, and deleting `downloads/<id>` cancels
the torrent and takes the files with it. Every route the agent has ends in either a
duplicate or a destroyed download.

**Have the completion sweep cancel the torrent when it finishes.** Then no torrent is
listed, the overlay owns nothing, and the move is already allowed. Rejected because it
makes the media server decide seeding policy for the download client, and it discards the
one thing the torrent is still doing. It also leaves the window between finishing and
sweeping refusing the move for no reason.

**Ask the pair rather than the path** — refuse only moves that leave `downloads/<id>`,
allow moves within it. Rejected: it does not describe the problem. What a finished
download's files need is to leave, and what a running download's files must not do is
move at all, in either direction, because the writer follows the path it was given.

## Consequences

- The organisation the completion alert asks for works on the same mount: a finished
  download's file, or its whole `downloads/<id>` directory, moves into the library while
  the torrent keeps seeding. The rendered status file stays where it is and disappears on
  its own once no download owns the id.
- A cross-mount move of a finished download's *directory* now passes the move-out check
  and completes: the bytes stream, and the tail delete on `downloads/<id>` cancels the
  torrent and removes what is left. A finished download's individual payload file still
  cannot leave the mount, because `fs_delete` refuses it and ADR-0015 asks the delete
  question before the first byte. Copying it off is unaffected.
- `Land` and `MoveOut` no longer share a predicate, so the mount can be asked "is this
  path a download's?" and answer differently depending on what is being done to it. That
  is what one rule per intent already meant; this is the first intent pair to use it.
- The refusal message changed from "belongs to a live download" to "belongs to a download
  that has not finished", because the first is now false of a path that moves fine.
