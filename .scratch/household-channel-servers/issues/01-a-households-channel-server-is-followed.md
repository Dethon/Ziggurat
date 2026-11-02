# 01: A household's channel server is followed through its own tunnel

**What to build:** The tracer bullet. A channel endpoint named by server name is followed for every
household, not only the home. Each household's opening of that tunnel is its own connection, and the
connection carries its household. The connection host runs one connection per opening per household;
a newer opening finishes only that household's older run. The monitor reads the live set of
connections, not the list it was built with. A message from a household's connection is believed only
for that household: a turn whose verified household (assertion, guest or delegation) differs is
refused and logged before any agent is built. The interactive reply goes back on the connection the
message came from. A non-home household's channel notification, read through its tunnel, is answered
by its own agent and replied to through the same tunnel. Spec: `../spec.md`.

**Blocked by:** none

**Status:** ready-for-agent

- [ ] A non-home household's channel notification is answered through its own tunnel (`TunnelledChannelTests`, a second household)
- [ ] Two households each running a channel server of the same name get their own replies, never each other's
- [ ] A message asserting another household than its connection's is refused, and no agent is built for it
- [ ] A non-home tunnel's drop and reopen is followed without a restart
- [ ] Connection host: one run per opening per household; two households' openings run side by side; a newer opening finishes only its own household's run (`ChannelConnectionHostTests`)
- [ ] The home's channels behave as before (the existing tunnelled channel and host tests stay green)
- [ ] `.claude/rules/tunnel.md` "A household's channel server" describes following per household
