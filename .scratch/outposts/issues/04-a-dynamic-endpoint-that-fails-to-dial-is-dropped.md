# 04 — A dynamic endpoint that fails to dial is dropped

**What to build:** A session survives a dynamically registered endpoint that will not answer.

Today every endpoint is dialled together and a single one that never responds fails the whole
session, after roughly fourteen seconds of retrying. That is correct for a container in the
compose stack, where being unreachable is a real fault. It is wrong for a machine that registered
itself, because a laptop that shuts its lid is reachable and then not, and nobody has done
anything wrong. Under the current rule one closed lid would cost an agent its vault, its sandbox
and its home automation.

After this ticket, a dynamic endpoint that cannot be dialled is logged and dropped, the session is
built from everything else, and that machine's mount is simply absent. A configured endpoint that
cannot be dialled still fails the session, loudly, as it does now.

**Blocked by:** 03 — An endpoint carries its origin.

**Status:** done

- [x] The dial policy reads the endpoint's origin, and nothing else, to decide which rule applies.
- [x] A session containing one live and one dead dynamic endpoint builds, exposing the live one's
      tools and mount and neither of the dead one's.
- [x] A session containing one live and one dead configured endpoint fails.
- [x] A dropped dynamic endpoint is logged with enough to identify which machine it was.
- [x] The dead endpoint does not delay the mounts that did come up beyond what it costs to give up
      on it.
- [x] Tests drive thread session construction, using the existing MCP server fixture for the live
      endpoint and an unused port for the dead one, covering both roles.
- [x] The reasoning is already recorded in ADR-0027; the code does not restate it, it points at
      it.
