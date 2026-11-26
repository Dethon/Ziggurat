# 11: The apps skill teaches asks

Spec: `.scratch/app-asks/spec.md` ("The `apps` skill"; "Testing Decisions", seam 3).

**What to build:** The building agent learns asks from the `apps` skill:
- writing instructions, and declaring `asks` and `model`;
- strict schemas, files and follow-ups;
- the error codes and the cap;
- checking an app that asks.

One Chromium case proves the served library end to end.

**Blocked by:** 03, 04, 05, 06, 07, 08, 09, 10

**Status:** ready-for-agent

- [ ] The skill teaches every new manifest field, `apps.ask` option, result shape and error code. `AppsSkillTests` holds each name to the code.
- [ ] The skill stays within its declared budget (raise the declaration deliberately if needed).
- [ ] A Chromium case in the browser serving tests: a served page asks with a schema and an image, follows up, and gets an `AskError` on the cap.
- [ ] `.claude/rules/apps.md` (and the channel and memory rules where they changed) states the new contracts.
