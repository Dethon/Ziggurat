# 08 — The fire is proven end to end

**What to build:** One test against the real Home Assistant container proves the whole bridge: the seed provisions the watch-fired rest_command towards a listener the test owns, the test writes a prompt watch on a sensor through the mount, pushes the sensor's state across the threshold through the REST states endpoint, and the listener receives the payload with the watch id, the rendered prompt and the firing facts. With that in hand the ADR moves from proposed to accepted and the memory note says the feature shipped.

**Blocked by:** 05 — The home can raise a prompt with the agent; 06 — A prompt watch.

**Status:** done

- [x] Integration test (the timer ring end-to-end test is the prior art): state change → automation fires → listener receives the payload; a second change in the same direction does not fire again (crossing semantics), a change back and across does.
- [x] The seed's configuration carries the rest_command and a full container start is what the fixture already does.
- [x] ADR 0038 status is accepted; the bridges document's "checking it" section covers a watch.
- [x] Spec: `.scratch/home-watches/spec.md` § Testing Decisions, seam 3.

## Comments

- 2026-09-05: `HomeAssistantFixture` owns the far end — a listener bound on every interface, its
  url seeded into `rest_command.assistant_watch_fired` before the container starts, the container
  given `host.docker.internal` as its host gateway. Verified green on WSL2 with Docker Desktop.
