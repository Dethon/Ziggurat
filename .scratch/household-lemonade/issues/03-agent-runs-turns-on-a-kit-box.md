# 03: The agent runs a household's turns on its kit box

**What to build:** A household whose keys section says its Lemonade box is reached **through the tenant kit** has that box's models in its own picker, and its turns on them run through its `lemonade` tunnel. The keys section's box takes one of two shapes: at an address (`apiUrl` and an optional `apiKey`, as today) or `throughKit: true` with nothing else. The agent view and the `lemonade-boxes` view carry the shape. For a kit box, the agent's per-household Lemonade hosts build a host on a fixed synthetic base address, with ticket 01's transport onto that household's `lemonade` server and `chat` tag. The box's model discovery and the turns' Lemonade chat client share that transport, and subagents run on their parent's box as now. The agent never sends a key down the tunnel.

**Blocked by:** 01, 02.

**Status:** done

- [ ] The keys section's checks refuse a box with both an address and `throughKit`, and one with neither. The address rule (public https for any household but the home) applies only to the address shape.
- [ ] The agent view's `lemonade` and the `lemonade-boxes` view carry a kit box as `throughKit`, with no key.
- [ ] Integration test with the relay host, the real forwarder from 02 and a stub box (the household's keys section is written directly):
  - after a refresh, the household's hosts list the stub's models, and another household's hosts don't;
  - a chat call through the household's host streams the stub's reply chunk by chunk;
  - the stub sees the forwarder's key and never one from the agent;
  - closing the tunnel empties the models on the next refresh and fails a chat call; reopening the tunnel restores them;
  - a second household with its own `lemonade` tunnel and box never reaches the first's box, and the reverse.
- [ ] Switching a household's box between the address shape and the kit shape gets a fresh discovery on the next refresh, as an address change does now. The household Lemonade hosts unit tests cover it.
- [ ] The home with a kit box runs on it rather than the deployment's configured box. The home with no box of its own keeps the deployment's.
- [ ] The household configuration rules describe the keys section's two shapes and what the views carry, and the routing rules' "A Lemonade box is its household's" mentions the kit way in.
