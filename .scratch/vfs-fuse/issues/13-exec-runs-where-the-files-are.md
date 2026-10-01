# 13 — exec runs where the files are

**What to build:** `exec` on a mount without a shell (timers, schedules, Home Assistant) runs in the sandbox, with that virtual path as cwd, when the session has a sandbox. Without a sandbox it falls back to the mount's own catalog, so `./dismiss` works for every agent. A rerouted exec takes the sandbox's `Contained` reach in the exec screen. The exec tool's and the sandbox's descriptions say which mounts the shell reaches and at which path, including any served only under `/vfs`, and that `vfsChanges` lists what a command changed.

**Blocked by:** 12 — Actions run from any script

**Status:** ready-for-agent

- [x] For a session with a sandbox, `exec ./dismiss` on `/timers` runs in the sandbox and dismisses the timer
- [x] For a session without one, the same call runs the timer mount's catalog with the same effect
- [x] The screen judges a rerouted exec at `Contained` reach; an outpost's exec is still `Host`
- [x] The descriptions name the reachable mounts, any `/vfs`-only ones, and `vfsChanges`; snapshots cover them
