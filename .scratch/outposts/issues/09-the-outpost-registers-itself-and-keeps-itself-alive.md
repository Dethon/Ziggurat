# 09 — The outpost registers itself and keeps itself alive

**What to build:** The machine side of registration. Running the binary is now the whole
installation: it announces itself to the hub, keeps announcing while it runs, and takes its
announcement back when it is stopped. Nothing has to be added to any configuration file.

It works out its own address rather than being told, because the person starting it should not
have to know which network interface the hub will reach them on. It serves whether or not the hub
is up, because a laptop that boots before the stack, or connects to the VPN a minute late, should
not be permanently absent for that reason.

**Blocked by:** 05 — The outpost serves a machine's filesystem. 08 — The hub records a
registration and lets it expire.

**Status:** done

- [x] Flags for the hub's address and for overriding the advertised address. The advertised
      address is otherwise worked out from the route toward the hub, which is right on a flat
      network and wrong on a multi-homed one, which is what the override is for.
- [x] Neither autodetection nor the override yielding a usable address is a startup failure with
      a message that says so, rather than a registration of something nothing can reach.
- [x] The listener comes up regardless of whether the hub answers.
- [x] Registration retries with backoff, forever. A hub that comes back finds the machine already
      there. A failed keepalive is just the next retry.
- [x] A keepalive every thirty seconds, against the hub's ninety second expiry.
- [x] Stopping the binary takes the registration back, so a machine turned off deliberately
      disappears at once instead of ninety seconds later.
- [x] The shared secret is presented on every call, and is the one value that arrives as an
      environment variable rather than a flag.
- [x] Verified by hand: start it, watch the registration appear; stop it, watch it go; kill it
      without warning, watch it lapse.
