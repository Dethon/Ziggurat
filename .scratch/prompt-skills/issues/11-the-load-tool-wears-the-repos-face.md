# 11 — The load tool wears the repo's face

**What to build:** The framework builds `load_skill` with a generic description and a free-text `skillName`; on about 3% of eval runs (7 of 228 in the last full pass) the model calls it as a warm-up probe, first call of the turn, sometimes with garbage names (`?`, `? no skill needed`) and sometimes with `home-watches` on a turn that needs no skill. `SkillsProvider` (`Infrastructure/Agents/Skills/SkillsProvider.cs`) already rewrites the provider's tool list to drop the resource and script tools; at the same seam, replace the load function with a delegating one that keeps the framework's invocation and changes its face: a description of our own ("Loads one skill from the `<available_skills>` list. Call it only when the request is of that skill's kind, once per conversation, never to check what it does or to fill a pause."), and a parameter schema whose `skillName` is an `enum` over the session's advertised names, so a garbage name is impossible at the schema level. The name stays `load_skill` (`SkillsProvider.LoadToolName`, which the eval reads). Then one full-tier pass, and read the spurious-load count against 7 of 228: Francisco accepts the reflex at or under 5%; this ticket exists to see whether the shape brings it down.

**Blocked by:** None — can start immediately. Do this before 05.

**Status:** done

- [x] `McpAgentSkillsTests` shows the load tool's description is the repo's and its schema lists exactly the session's skill names as an enum; a load with a listed name still returns the body; the resource and script tools are still absent.
- [x] The prompt snapshots are unchanged (the tool's face is not in the instructions).
- [x] One full-tier armed pass (`ZIGGURAT_EVAL=1 dotnet test --filter "Category=Eval&Tier=Full"`, launched with `setsid nohup` and a log — the Bash tool's ten-minute cap kills a backgrounded run; never rebuild while it runs). Back up `.eval-output/scorecard-full.json` first. Count the `unnecessary call: load_skill` lines across `.eval-output/*.md` and `.eval-output/flakes/*.md` newer than the backup, and note the count in the commit against 7 of 228.
- [x] Spec: `.scratch/prompt-skills/spec.md` § Transport and ownership; the decision on the rate is in `handoff.md`.

Result (2026-09-08): 73/73 scenarios, 223/228 runs; `unnecessary call: load_skill` on 2 of 228 runs (both `home-watches` on vault turns) against 7 of 228 before. Scorecard kept as `.eval-output/scorecard-full.2026-09-08.after-load-tool-face.json`.
