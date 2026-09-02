# 01 — The two-window rule is shared

**What to build:** nothing a person can see changes. The rule that cuts a Lemonade turn to the smaller of the agent's context window and the window the box loaded the model with, today a private function of the agent factory, moves to a shared home that both the factory and the memory module can read. The factory calls the shared rule; a model whose window the box did not report still fits the caller's window, and a caller with no window of its own fits the discovered one. This is the prefactor that lets the extraction ticket reuse the rule instead of copying it.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The window rule lives in one shared place with the three cases named in the spec: both known → the smaller; discovered unknown → the caller's; caller unknown → the discovered.
- [ ] The agent factory reads the shared rule and its own private copy is gone.
- [ ] The existing Lemonade turn fit tests pass unchanged.
- [ ] The shared rule has its own unit test for the three cases, driven as a pure function.
- [ ] `dotnet format` clean; no other behaviour changes.
