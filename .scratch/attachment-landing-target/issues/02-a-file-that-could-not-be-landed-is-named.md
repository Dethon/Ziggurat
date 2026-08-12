# 02 — A file that could not be landed is named

**What to build:** When a file cannot be put into the sandbox, the model is told which files,
so a turn that lost them says so instead of planning commands against paths that do not exist.
Today the failure is a log line nobody reads: the message records no landed path, hydration adds
nothing, and the model answers from the bytes as context with no idea a file was meant to be
there.

Two cases produce it — a write that fails, and an exec-capable mount that declares no workspace
and therefore lands nothing. Both are the same thing to the model: named files it cannot act on.

**Blocked by:** 01 — the undeclared-workspace case cannot be expressed before a mount can
declare one, and both tickets change what landing returns.

**Status:** ready-for-agent

- [ ] A file whose write fails is named to the model on the way out, through the same step that
      already names the files that landed.
- [ ] An exec-capable mount that declares no workspace lands nothing and the model is told which
      files could not be placed.
- [ ] A partly landed message names only the files that failed and still reports the ones that
      landed.
- [ ] The failed names are recorded on the message alongside the landed paths, so the turn's
      record is complete.
- [ ] The failure notice is spoken within the hydration distance and silent past it: beyond that
      distance the model has neither the bytes nor the file, and a notice about neither is noise.
- [ ] Landed paths keep their unbounded life, because the file is still there.
- [ ] Asserted at the hydration depth seam, beside the existing cases for an attachment within
      the distance and the placeholder beyond it.
