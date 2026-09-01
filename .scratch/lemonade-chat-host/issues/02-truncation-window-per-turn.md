# 02 — The truncation window is decided per turn

**What to build:** The chat client stops fixing the context window it truncates to at construction and instead decides it per call, from the turn it is about to send. Today every turn still gets the agent's window, so nothing a user or a metric can observe changes. This is a prefactor: a later ticket makes a Lemonade turn truncate to the smaller of the agent's window and the model's discovered window, and it must be able to do that without rebuilding the client.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] Truncation for a turn reads a window resolved for that turn, and with no override the window is the agent's, exactly as today
- [ ] The metric events that report the window used still report the agent's window for every turn
- [ ] Existing chat client, truncation and multi-agent factory tests stay green, with any test that pinned the construction-time window moved to the per-turn shape
- [ ] No behaviour change for any provider or model
