# 11 — The keepalive reports the mount verdict

**What to build:** Feedback at the machine. Someone who runs an outpost and finds it never shows
up can now see why, on the computer they are sitting at, instead of needing access to the hub's
logs.

There is exactly one way for an outpost to be registered and still not be there: its name is
already some other mount's, so it is shadowed. Nothing at the machine can detect this, because the
collision is discovered on the hub when a session is built, long after the registration succeeded.
The keepalive is the only channel back.

**Blocked by:** 10 — An opted-in agent mounts live outposts.

**Status:** done

- [x] When a session is built, each outpost's verdict — mounted, or shadowed — is written onto its
      registration.
- [x] The keepalive's answer carries the verdict, so the machine learns it within one interval of
      the hub knowing it.
- [x] The outpost logs the verdict locally, and logs a shadowed verdict prominently enough that
      somebody watching the process sees it.
- [x] A verdict of "not yet known", before any opted-in agent has built a session, is
      distinguishable from "mounted" and from "shadowed", and does not read as a problem.
- [x] The keepalive stays a liveness ping carrying one verdict. It does not grow into a reporting
      channel: the outpost still sends no telemetry of its own.
