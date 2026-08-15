# 08 — The hub records a registration and lets it expire

**What to build:** The hub side of registration. Something can now announce that it is a live
outpost at a given address, keep that announcement alive, take it back, and have it lapse on its
own if it stops asking.

Nothing consumes the registrations yet and no binary calls these endpoints yet. Both arrive in
later tickets. This one is verifiable on its own with a shell and a clock.

The registration expires because Redis expires it. There is no reaper, no sweep and no timer on
the hub: an outpost that stops asking simply stops existing, which is what a machine losing power
looks like from here.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] Three endpoints on the Agent, which already hosts an HTTP API and already registers custom
      agents at runtime: announce a registration, refresh it, take it back. They stay one-liners
      over the registry, exactly as the custom-agent endpoint is today.
- [ ] One shared bearer secret gates all three. It is a secret, so it gets a placeholder in the
      compose env file wired through as a variable, never a real value.
- [ ] One Redis key per outpost registration, refreshed by the keepalive, with a ninety second
      expiry against a thirty second refresh interval.
- [ ] The registration's name is its identity and the last write wins, so a machine that restarts
      re-registers over its own entry with no special handling.
- [ ] Taking a registration back removes it immediately rather than waiting for expiry.
- [ ] A metric is published when a registration lands, is refreshed and expires, so the question
      "was that machine up at two o'clock" is answerable.
- [ ] Unit tests drive the registry type over an injected store and a controllable clock: land,
      refresh, expire, re-register over a live entry, take back. This mirrors how the agent
      definition provider is tested rather than its endpoint.
- [ ] One integration test on the shared Redis fixture proves the key really disappears. The
      expiry is the entire mechanism, and a wrong argument would otherwise ship green.
