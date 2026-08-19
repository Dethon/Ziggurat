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

- [x] The scheduling server runs in-process alongside the timers server, sharing the pinned
      instant, and the calendar is reachable through the Home Assistant fake.
- [x] A duration-phrased request that tells a person creates a countdown and touches neither the
      calendar nor the schedules.
- [x] A duration-phrased request that requires the agent to act creates a scheduled one-shot with
      the correct absolute time.
- [x] A clock-time request creates a calendar entry and no countdown.
- [x] A request past the four-hour ceiling does not create a countdown.
- [x] A voice turn with a satellite targets the speaking room without being told.
- [ ] A turn with no satellite asks which room and creates nothing. **Green on 2026-08-18 and
      withdrawn on 2026-08-19**, when ticket 14 gave the eval the memory feature both shipped
      assistants enable — until then the suite had been running a prompt one section short. With
      that section present the same turn creates a kitchen timer instead of asking, two runs out
      of three; with it removed the scenario passes. Isolated against the websearch server too,
      which changes nothing either way. The rule is stated in `TimerPrompt` and is not followed, so
      the scenario is gone and the finding is in `ClaimExemptions`.
- [x] Every scenario cites its claims, and each was demonstrated red by deleting that claim's
      prose. Eight demonstrations were run on 2026-08-18 and six stayed green: only the four-hour
      ceiling and the ask-which-room rule were load-bearing on these turn shapes — and the second
      of those is withdrawn as of 2026-08-19, leaving one. The other six
      scenarios keep running as regression guards with no citation, and each exemption in
      `ClaimExemptions` now carries what the demonstration showed instead of what it was waiting
      for.
