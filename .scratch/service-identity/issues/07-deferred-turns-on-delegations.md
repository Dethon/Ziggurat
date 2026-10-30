# 07: Deferred turns act on agent-signed delegations

**What to build:** A schedule's fire, a login wake and a watch's fire act for a person only because that person's turn created them. When a turn creates or rewrites a schedule, starts a login handoff or sets a watch, the agent signs a delegation naming the account, tenant, kind, id and a digest of what the object does, and the server stores it; at fire time the agent verifies its own signature, the object and the digest. A command re-delegates stored objects after the agent's key is lost. Spec: `../spec.md` ("Deferred turns").

**Blocked by:** 04, 05

**Status:** done

- [x] A schedule's fire acts for its writer on a stored delegation, and as nobody once another turn rewrites it without one or its digest no longer matches
- [x] A deleted schedule's delegation is gone
- [x] A login wake and a watch fire act for the person whose turn started them, and a forged one is refused
- [x] A download's fire stays nobody's
