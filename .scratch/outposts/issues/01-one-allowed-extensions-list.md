# 01 — One allowed-extensions list

**What to build:** The list of file extensions that count as text exists once, in `Domain`, and
the sandbox server reads it from there instead of carrying its own copy in its settings file.

Nothing changes for anyone using the agent. This exists so that when the outpost arrives it reads
the same list, and a file that is readable on the sandbox cannot be unreadable on a laptop for no
reason anybody can find.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] The extension list lives in `Domain` as a single definition, in the same place a second
      filesystem server would naturally reach for it.
- [x] The sandbox server's settings no longer contain the list, and the sandbox mount still
      reads and writes exactly the file types it did before.
- [x] A server can still override the list for itself, because the outpost will need to.
- [x] Existing sandbox filesystem tests pass unchanged. If any of them assert the list's contents
      by literal, they now assert against the shared definition.
