# 01: A hub turn can be sent from a stored snapshot of its sender

**What to build:** A prefactor with no behaviour change. Today the chat hub builds a person's turn and vouches for it from the live SignalR connection in hand: the connection's session, principal, display name and space. Make the same send possible from a **sender snapshot** taken when a message is accepted. The snapshot holds:
- the session id the connection's assertion named;
- the principal;
- the display name;
- the topic's conversation identity, agent and space.

Building the channel notification and vouching through the session accounts service then needs only the snapshot and no hub call in progress. `SendMessage`, `SendRerun`, `SendContinue` and `EnqueueMessage` go through the new path, so a later dispatch from outside the hub (when a turn completes on the stream) sends exactly what a live send does.

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] Every existing send path builds its notification and vouches from a sender snapshot.
- [ ] A snapshot sends the same notification as the live send it was taken from: same sender, principal, assertion, agent, patch and attachments.
- [ ] A session revoked after the snapshot was taken is refused "signed out" when the snapshot is sent, and an unreachable accounts service answers "could not be checked", both on the stream as today.
- [ ] The existing chat hub tests stay green unchanged.
