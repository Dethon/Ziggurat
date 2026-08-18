# 07 — Timer vs calendar reminder vs scheduled action

**What to build:** the three-way discrimination that the timer contract exists for, as scenarios.

A duration under four hours where a person is told something becomes a countdown. A clock time, a
date, anything recurring, or anything past the ceiling goes to the alarms calendar. Anything where
the agent itself must act at the appointed moment — "apaga el aire en una hora" — becomes a
one-shot scheduled action with an absolute time, however the time was phrased.

Two more cases the contract states and nothing verifies: a voice turn defaults to the speaking
room, and a turn with no satellite must ask which room rather than guess.

**Blocked by:** 03, 04, 05.

**Status:** ready-for-agent

- [ ] The scheduling server runs in-process alongside the timers server, sharing the pinned
      instant, and the calendar is reachable through the Home Assistant fake.
- [ ] A duration-phrased request that tells a person creates a countdown and touches neither the
      calendar nor the schedules.
- [ ] A duration-phrased request that requires the agent to act creates a scheduled one-shot with
      the correct absolute time.
- [ ] A clock-time request creates a calendar entry and no countdown.
- [ ] A request past the four-hour ceiling does not create a countdown.
- [ ] A voice turn with a satellite targets the speaking room without being told.
- [ ] A turn with no satellite asks which room and creates nothing.
- [ ] Every scenario cites its claims, and each was demonstrated red by deleting that claim's
      prose.
