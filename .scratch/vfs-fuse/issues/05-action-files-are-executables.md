# 05 — Action files are executables

**What to build:** Every action file loses its `.sh` suffix: the timer's `dismiss`, the schedule's `run_now`, and each Home Assistant service, catalog or served (ADR 0036). Action files are refused on read and write on every route, and info and glob mark them executable through a defaulted flag, so other backends are untouched. `./dismiss` is the spelling taught, because a real shell will need it once exec is rerouted into the sandbox; the catalog accepts it and the bare name alike.

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

- [x] On each executing mount, exec of `./<action>` and of `<action>` runs the action
- [x] The old `<action>.sh` answers not-found and lists the available names
- [x] Reading or editing an action file is refused with an envelope that says it is executable-only
- [x] Info reports an action file as executable, and glob marks it the same way; other backends' answers are unchanged
- [x] Prompts, skills and their snapshots teach `./<action>`
- [x] The timer, scheduling, Home Assistant and voice rules use the new names
