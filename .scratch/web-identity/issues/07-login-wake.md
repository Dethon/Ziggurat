# 07: Login wake

**What to build:** Pressing Done wakes the agent in the conversation the handoff came from, so the task carries on with nobody typing "done". The browse server becomes a channel server as well as a tool server (as Sugary is), with `BufferAlways` so a wake arriving while the agent reconnects is kept, and the agent dials it as a channel endpoint. Done emits a channel message carrying the original conversation id, agent id and user id, `ReplyTo` the original reply target, and a new origin kind `Login`; its prompt names the site and that the person is now logged in. Joining by the original conversation id, it queues behind a running turn of a live conversation group. Spec: `../spec.md` ("Done and the wake").

**Blocked by:** 06

**Status:** done

- [x] Done emits exactly one wake with the original conversation id, reply target and `Login` origin (drained inbox test, prior art: the watch-fired endpoint tests)
- [x] An abandoned or expired handoff emits nothing
- [x] The browse server's registration row moves to the channel role and passes the channel receive contract tests
- [x] The agent's channel endpoints include the browse server
- [x] A turn woken by a login carries origin kind `Login` and so cannot start another handoff
