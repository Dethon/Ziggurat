# 01: A channel connection knows its household, and the monitor reads a live set

**What to build:** Prefactoring, with no change anyone can see. Every channel connection carries the
household it serves: the home for the home's dual-role servers, every household for a channel dialled
by URL (the chat client, Telegram). The channel monitor reads a set of connections that can grow and
shrink while it runs, instead of the list it started with.

Everything that picks a connection by channel id asks for it by channel id and household. That covers
replies, fan-out delivery, the review copy's host, the topic created ahead for an attach-only target,
attachment fetches, the approvals relay and the alerts relay. With only the home's tunnels followed,
every answer is the one it is today. Spec: `../spec.md`.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] The existing tunnelled channel round trips, browse page and redelegation tests stay green, unchanged
- [x] A connection added to the live set after the monitor started is read (round trip: the home's channel server's tunnel opens after the agent started, and its notification is answered)
- [x] A connection whose tunnel closed is not connected, with the five behaviours ADR 0011 keeps
