# 12 — Actions run from any script

**What to build:** `/timers/dismiss`, `/schedules/<id>/run_now` and Home Assistant actions run from a bash or Python script, with the action's real output and exit code.

- Every action file is served as one static Rust helper with mode `0111`. The kernel executes it, and user code can never read it.
- The helper holds no token. It hands its virtual path, arguments and cwd to the call's daemon over a socket inside the namespace.
- The daemon checks the caller belongs to this exec, commits every held new file first, and runs the mount's exec through the bridge.
- Each action is listed in `vfsChanges`.

**Blocked by:** 05 — Action files are executables; 08 — The shell writes files on mounts

**Status:** ready-for-agent

- [ ] `/timers/dismiss` from a bash script stops ringing exactly as a direct exec does, with its output and exit code passed through
- [ ] The same action called through Python's `subprocess` behaves the same
- [ ] `cat` of an action file is refused
- [ ] A file created earlier in the same script is visible to the action
- [ ] A helper started outside an exec's namespace is refused
- [ ] Each action is listed in `vfsChanges` as an action
