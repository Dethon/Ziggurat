# 01 — A timer with no room is created rather than asked about

**Status:** resolved

**Where it was found:** the behavioural eval, 2026-08-19. The scenario asserting it was written on
2026-08-18, was green, and was withdrawn when the eval was made faithful — see
`.scratch/behavioural-eval-harness/issues/07-timer-calendar-scheduled-action.md` and the entry for
`timers.no-satellite-asks-which-room` in `Tests/Eval/ClaimExemptions.cs`.

**What the contract says.** `TimerPrompt` states that a turn arriving with no satellite has no
speaking room to default to, so the agent asks which room the timer should ring in rather than
guessing one.

**What happens.** "pon un temporizador de ocho minutos para la pasta", sent by a chat user with no
room and no satellite on the turn, creates `/timers/pasta/timer.json` targeting the kitchen and
replies "Temporizador de ocho minutos puesto para la pasta." Two runs out of three.

**What makes it interesting.** It is an interaction between two prompt sections rather than a
weakness of either. With the memory section removed from the assembled prompt the same turn asks
the question; with it present it guesses. Both shipped assistants enable the memory feature, so
the guessing behaviour is what the deployment does. Hosting the websearch server changes nothing
either way — that was isolated separately.

**Why it matters.** A guessed room rings in an empty room. The user hears nothing, and the timer
looks armed.

**What a fix might look like** (not decided): strengthen the rule where it is written, or move the
no-room case out of `TimerPrompt` and into whatever section is read closest to the conversation.
Whatever is tried, the withdrawn scenario is the acceptance test — restore it from git history at
`27b50d3a^:Tests/Eval/Scenarios/MechanismScenarios.cs`.

## Comments

2026-08-19 — Fixed by strengthening the rule where it is written. The no-room bullet in
`TimerPrompt` now closes the escape hatches the memory section opens: on a channel with no
speaking room, nothing else supplies one — not what the timer is for, not anything remembered
about the user, not a room used before. The withdrawn scenario is restored from
`27b50d3a^` as the acceptance test and the `timers.no-satellite-asks-which-room` exemption is
removed. The deterministic harness is green; whether the strengthened prose actually holds
against the model is an eval run (`ZIGGURAT_EVAL=1`), which this fix has not spent.
