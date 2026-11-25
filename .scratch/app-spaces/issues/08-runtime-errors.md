# 08: Runtime errors

Spec: `.scratch/app-spaces/spec.md` ("Runtime errors").

**What to build:** When a person says an app is broken on their phone, the agent can see why. The client library reports uncaught errors, unhandled rejections, and failed or refused reads to `/_errors` on the app's origin. The apps server keeps the newest N per app, serves them read-only at `/apps/<slug>/errors` newest first, and logs them as household errors the operator sees.

**Blocked by:** 06

**Status:** ready-for-agent

- [ ] Each entry carries message, stack, route, user agent, time and the viewer's person id. A read failure also carries its virtual path and envelope. No entry ever carries data that was read.
- [ ] Only a signed-in viewer of that app can report, and entries land on that app alone.
- [ ] The kept count is an appsettings tunable, and older entries drop.
- [ ] The errors are visible on the operator's side as household errors.
