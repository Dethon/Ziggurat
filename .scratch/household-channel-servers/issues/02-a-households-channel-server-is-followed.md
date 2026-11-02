# 02: A household's channel server is followed through its own tunnel

**What to build:** The tracer bullet. A dual-role server named by its server name is followed for
every household, not only the home. Each household's tunnel for it is a connection of that
household's.

A notification raised by a non-home household's own channel server is answered by that household's
agent, and the reply goes back through the same tunnel. A message whose verified household (a
person's assertion, the guest's, or a fire's delegation) differs from its connection's household is
refused and logged with both households, before any agent is built. A household's tunnel that drops
and reopens is followed again without a restart, and one household's reconnect never disturbs
another's. The tunnel rules' section on a household's channel server is rewritten. Spec: `../spec.md`.

**Blocked by:** 01

**Status:** done

- [x] A non-home household's notification is answered by its own agent through its own tunnel (round trip, a second household)
- [x] Two households with a channel server of the same name each get only their own replies
- [x] A message asserting another household is refused, logged with both households, and no agent is built
- [x] A non-home tunnel's drop and reopen is followed without a restart, and leaves another household's connection alone
- [x] Revoking the credential or suspending the household disconnects it; resuming brings it back by itself
- [x] The home's channels behave as before
