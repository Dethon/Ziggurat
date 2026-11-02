# 02: Delivery, approvals, alerts and attachments stay in the household

**What to build:** Everything that picks a channel connection by id now picks it by household too.
For every channel id, the candidate connections are the deployment's own connection (a URL endpoint,
which serves every household) and the turn's household's own connection. This applies to:
- a fan-out's targets (`deliverTo` of a schedule, a watch or a login wake);
- the review host;
- the topic pre-minted for an attach-only target;
- an attachment fetch, from the connection that took the file;
- the approvals relay's offers and settlements;
- the alerts relay's rings.

None of them ever takes "the first connection with that id". Each connection is told its own
household's agent catalogue (`HouseholdAgentCatalogue`), not the home's. `/internal/topics` refuses an
assertion whose household is not the household of the tunnel the request came through. Spec:
`../spec.md`.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] A household's fire delivering to `voice` is spoken on that household's voice server, never another's (`DeliveryTargetResolver` tests, then a tunnelled round trip)
- [ ] An approval is offered and settled only on the deployment's channels and the asker's household's connections (`ApprovalRelayTests`)
- [ ] An alert rings only on the deployment's channels and the owner's household's connections (`AlertRelay` tests)
- [ ] An attachment is fetched from the connection of the household that took it, and the cache key includes that household
- [ ] A connection is registered with its household's agents, not the home's
- [ ] Topic minting through a household's tunnel refuses another household's assertion
