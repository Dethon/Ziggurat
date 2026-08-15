# 06 — A jailed outpost refuses and roots its walks

**What to build:** A flag that confines an outpost to its working directory, so a person can
expose one project without exposing their whole home directory.

A jailed outpost is still mounted at the machine's root. Jailing changes what an operation will
do, never what a path is called, so the same file has the same name whether the outpost is jailed
or not. Asking for something outside the working directory comes back as a refusal, not as an
empty result, so the model can tell a jail from an empty directory and say which it hit.

**Blocked by:** 05 — The outpost serves a machine's filesystem.

**Status:** done

- [x] A jailed flag, and one refusal rule that every operation asks before it acts, in the shape
      the media library already uses for live downloads.
- [x] Every path argument outside the working directory is refused: above it, elsewhere on the
      machine by absolute path, and by any spelling that resolves outside it.
- [x] Glob and text search start their walk at the working directory rather than at `/`. Walking
      the whole disk and filtering afterwards would spend the scan budget on entries it is going
      to discard and report that the budget was reached for a reason the model cannot see.
- [x] An unjailed outpost allows every one of the above.
- [x] A transfer out of a jailed outpost obeys the same rule as any other operation on it.
- [x] Tests drive the domain filesystem tools against an outpost backend pointed at a temporary
      directory, asserting refusal envelopes with the shared assertion helper. The tool boundary
      is the seam, not the backend, because that is where the rule about which coordinates a
      response may use lives.
- [x] A refusal names the path in virtual coordinates, never the machine's own spelling.
